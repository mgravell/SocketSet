<#
.SYNOPSIS
    The Windows correctness gate: SmokeTest across IOCP / RIO / managed, plaintext and TLS.

.DESCRIPTION
    There are no unit tests in this repo; SmokeTest IS the correctness gate, and AGENTS.md asks for it on
    every backend touched. That instruction has always been carried out by hand, which is why it was
    skipped between OSes. This runs the whole matrix and reduces it to one PASS/FAIL line per cell.

    Written 2026-07-29 for a specific question: dd8cdce changed OutboundConnection.Flush to hand over the
    accumulator's own pooled array instead of renting a snapshot and copying into it. EpollConnection and
    WindowsConnection both derive from OutboundConnection, so IOCP and RIO inherited that change on Linux
    and have never executed it. The behavioural difference is that the handed-over array can be MUCH
    LARGER than the `length` argument (it grows by doubling) where the old snapshot was Rent(length), so
    a consumer inferring payload size from data.Length would over-send.

    That is why --verify is the sharpest cell here and why it runs at several payload sizes: it is the
    out-of-band Connection.Send harness, i.e. the Flush path, and it byte-verifies what arrived. A
    truncation or an over-send shows as a mismatch or a received != expected, not as a crash.

    TLS legs use --tls-schannel (real SSPI) rather than --tls (the no-crypto identity filter), because
    the identity filter shares the plumbing but not the record layer, and TLS is where the out-of-band
    flush path does most of its work.

.EXAMPLE
    .\Run-SmokeMatrix.ps1
.EXAMPLE
    .\Run-SmokeMatrix.ps1 -Filter "*rio*" -Verbose
#>
[CmdletBinding()]
param(
    # Cell name filter, e.g. "*rio*" or "*verify*". Cell names are "<backend><tls>/<test>".
    [string]$Filter = "*",
    [int]$FirstPort = 10500,
    # Per-cell wall-clock ceiling. A wedge is a failure mode this gate must report rather than hang on.
    [int]$TimeoutSec = 120,
    # How many times to run each CHURN cell. Not a paranoia knob: item 0e is an access violation that
    # strikes roughly one run in two, and this suite ran that cell ONCE - so the gate could not have
    # disproved a fix, and a real fault read as a flaky harness. Everything else here is deterministic
    # and runs once.
    [int]$ChurnReps = 5,
    [switch]$KeepLogs
)

$ErrorActionPreference = "Stop"

$repo = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $repo "SmokeTest\SmokeTest.csproj"
$exe = Join-Path $repo "SmokeTest\bin\Release\net10.0\SmokeTest.exe"

Write-Host "building SmokeTest (Release) ..." -ForegroundColor Cyan
& dotnet build $proj -c Release -v q --nologo -f net10.0 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "build failed" }
if (-not (Test-Path $exe)) { throw "no SmokeTest.exe at $exe" }

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$logDir = Join-Path $PSScriptRoot "results\smoke-$stamp"
New-Item -ItemType Directory -Force $logDir | Out-Null

# Backend x TLS. Managed is included because it derives from the same OutboundConnection base and is what
# actually runs wherever a Windows backend is unavailable; it is cheap to cover and has its own TLS gate.
$backends = @(
    @{ Name = "iocp";    Args = @("--iocp") }
    @{ Name = "rio";     Args = @("--rio") }
    @{ Name = "managed"; Args = @("-m") }
)
$tlsModes = @(
    @{ Suffix = "";      Args = @() }
    @{ Suffix = "+tls";  Args = @("--tls-schannel") }
)

# The tests, in increasing order of what they would catch. Each entry is name + args + how to read the
# output; Pass is a regex that MUST match, Fail a regex that must NOT.
#
# verify-oob-* are the Flush hand-off cells. 7000-byte segments straddle the 4096 page boundary, and the
# three payload sizes bracket the accumulator's doubling: 64KB and 1MB both leave the handed-over array
# substantially larger than `length`, which is the exact condition the old snapshot path never produced.
$tests = @(
    @{ Name = "verify-oob-64k";  Args = @("--verify", "65536") }
    @{ Name = "verify-oob-1m";   Args = @("--verify", "1048576") }
    @{ Name = "verify-oob-4m";   Args = @("--verify", "4194304") }
    @{ Name = "echo-cb-64k";     Args = @("--verify-echo", "1048576", "-z", "65536") }
    @{ Name = "echo-pipe-64k";   Args = @("--verify-echo", "1048576", "-z", "65536", "--pipe") }
    @{ Name = "echo-pipe-4k";    Args = @("--verify-echo", "1048576", "-z", "4096", "--pipe") }
    @{ Name = "poke";            Args = @("-s", "-c", "8", "-t", "5", "--poke", "-z", "4096") }
    @{ Name = "churn";           Args = @("-s", "-c", "64", "--churn", "10", "--close-after", "4",
                                          "--sockets", "128", "--reset-close") }
)

# How each test's output is judged. "PASS"/"FAIL" is printed by the verify harnesses and by churn; the
# poke leg prints neither, so it is judged on having actually moved bytes.
function Test-CellOutput([string]$Name, [string]$Text, [int]$ExitCode = 0) {
    # A hard crash must be NAMED, not left to fall through as "no result line". The RIO+TLS churn cell
    # takes an intermittent access violation (TODO item 0e), and on the first run that reported as a
    # missing output line - which reads like a harness problem rather than the process dying.
    switch ($ExitCode) {
        -1073741819 { return @($false, "ACCESS VIOLATION (0xC0000005) - see TODO item 0e") }
        -1073740791 { return @($false, "STACK BUFFER OVERRUN (0xC0000409)") }
        -1073741571 { return @($false, "STACK OVERFLOW (0xC00000FD)") }
    }
    if ($Text -match "### UNHANDLED ###") { return @($false, "unhandled exception") }
    switch -Wildcard ($Name) {
        "verify-oob-*" {
            if ($Text -match "verify: received=(\d+)/(\d+) mismatches=(\d+) => (PASS|FAIL)") {
                $ok = $Matches[4] -eq "PASS"
                return @($ok, "received=$($Matches[1])/$($Matches[2]) mismatches=$($Matches[3])")
            }
            return @($false, "no verify line")
        }
        "echo-*" {
            if ($Text -match "verify-echo: roundtripped=(\d+)/(\d+) clientMismatch=(\d+) serverMismatch=(\d+) => (PASS|FAIL)") {
                $ok = $Matches[5] -eq "PASS"
                return @($ok, "rt=$($Matches[1])/$($Matches[2]) cm=$($Matches[3]) sm=$($Matches[4])")
            }
            return @($false, "no verify-echo line")
        }
        "churn" {
            if ($Text -match "churn: done .* live=(\d+) .*=> (PASS|FAIL[^\r\n]*)") {
                return @(($Matches[2] -eq "PASS"), "live=$($Matches[1])")
            }
            if ($Text -match "=> (PASS|FAIL)") { return @(($Matches[1] -eq "PASS"), "churn") }
            return @($false, "no churn result line")
        }
        "poke" {
            if ($Text -match "done: ([\d,]+) round-trip bytes") {
                $bytes = [int64]($Matches[1] -replace ",", "")
                return @(($bytes -gt 0), "$($Matches[1]) round-trip bytes")
            }
            return @($false, "no round-trip total")
        }
    }
    return @($false, "no rule for $Name")
}

$cells = @(foreach ($b in $backends) {
        foreach ($t in $tlsModes) {
            foreach ($x in $tests) {
                [pscustomobject]@{
                    Name    = "$($b.Name)$($t.Suffix)/$($x.Name)"
                    Args    = @($b.Args) + @($t.Args) + @($x.Args)
                    Test    = $x.Name
                }
            }
        }
    }) | Where-Object { $_.Name -like $Filter }

if (-not $cells) { throw "no cells matched filter '$Filter'" }

Write-Host ""
Write-Host "smoke matrix: $($cells.Count) cells -> $logDir" -ForegroundColor Cyan
Write-Host ""

$port = $FirstPort
$results = @()
foreach ($cell in $cells) {
    # Churn cells run N times and report the WORST outcome: an intermittent fault that a single run
    # would have called PASS is the whole reason item 0e survived this long.
    $reps = if ($cell.Test -eq "churn") { $ChurnReps } else { 1 }
    $ok = $true; $detail = ""; $worstSecs = 0.0; $failed = 0

    foreach ($rep in 1..$reps) {
        $port++
        $argList = @($cell.Args) + @("--port", "$port")
        $safe = $cell.Name -replace '[\\/:*?"<>|+]', '-'
        $out = Join-Path $logDir "$safe.r$rep.log"
        $err = Join-Path $logDir "$safe.r$rep.err"

        $sw = [Diagnostics.Stopwatch]::StartNew()
        $p = Start-Process -FilePath $exe -ArgumentList $argList -PassThru -NoNewWindow `
            -RedirectStandardOutput $out -RedirectStandardError $err
        $exited = $p.WaitForExit($TimeoutSec * 1000)
        if (-not $exited) {
            try { $p.Kill() } catch { }
            try { $p.WaitForExit(5000) | Out-Null } catch { }
        }
        $sw.Stop()
        if ($sw.Elapsed.TotalSeconds -gt $worstSecs) { $worstSecs = $sw.Elapsed.TotalSeconds }

        $text = ((Get-Content $out -Raw -ErrorAction SilentlyContinue) + "`n" +
                 (Get-Content $err -Raw -ErrorAction SilentlyContinue))
        if (-not $exited) { $repOk = $false; $repDetail = "TIMEOUT after ${TimeoutSec}s (wedged)" }
        else {
            $judged = Test-CellOutput $cell.Test $text $p.ExitCode
            $repOk = $judged[0]; $repDetail = $judged[1]
        }
        if (-not $repOk) { $failed++; if ($ok) { $detail = $repDetail } ; $ok = $false }
        elseif ($reps -eq 1) { $detail = $repDetail }
        if (-not $KeepLogs -and $repOk) { Remove-Item $out, $err -ErrorAction SilentlyContinue }
    }
    if ($reps -gt 1) {
        $detail = if ($ok) { "$reps/$reps clean" } else { "$failed/$reps FAILED - $detail" }
    }
    $sw = [pscustomobject]@{ Elapsed = [timespan]::FromSeconds($worstSecs) }

    $results += [pscustomobject]@{
        Cell = $cell.Name; Result = $(if ($ok) { "PASS" } else { "FAIL" })
        Detail = $detail; Secs = [math]::Round($worstSecs, 1)
    }
    $colour = if ($ok) { "Green" } else { "Red" }
    Write-Host ("  {0,-26} {1,-4} {2,6:n1}s  {3}" -f $cell.Name, $(if ($ok) { "PASS" } else { "FAIL" }), $worstSecs, $detail) -ForegroundColor $colour

    # Ports go into TIME_WAIT even with --reset-close on the accepted side; each cell gets a fresh one,
    # but give teardown a moment so a wedge is attributable to the cell that caused it.
    Start-Sleep -Milliseconds 300
}

Write-Host ""
$failed = @($results | Where-Object { $_.Result -eq "FAIL" })
$results | Format-Table -AutoSize | Out-String | Write-Host
if ($failed.Count -gt 0) {
    Write-Host "$($failed.Count)/$($results.Count) FAILED - logs in $logDir" -ForegroundColor Red
    exit 1
}
Write-Host "all $($results.Count) cells PASS" -ForegroundColor Green
