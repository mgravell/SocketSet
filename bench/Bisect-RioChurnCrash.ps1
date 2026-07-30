<#
.SYNOPSIS
    Narrow TODO item 0e (access violation in RIO+TLS under churn) by CONSTRUCTION, before reaching for a
    debugger. Each variant changes exactly one thing against a fixed baseline.

.DESCRIPTION
    This repo has settled a race this way before: item 0c looked like an io_uring teardown stall for days
    and fell in one afternoon to reading /proc/<pid>/wchan — no debugger, no symbols. The equivalent here
    is that the churn cell varies half a dozen things at once and every one of them has a flag, so each
    can be removed in isolation and the crash rate re-measured.

    WHAT EACH VARIANT WOULD MEAN IF THE CRASH DISAPPEARS — written down BEFORE running, so the result
    interprets itself rather than being interpreted afterwards:

      sockets 4096   The slot table is no longer tight, so a closed slot is not instantly re-tenanted.
                     Crash gone => slot REUSE is required, i.e. this is an ABA/lifetime race and the
                     suspect is per-slot RIO state (Rq, CommitPending/CommitRecv/CommitSend) outliving
                     the socket it belongs to. This is the hypothesis in item 0e.
      graceful close Removes SO_LINGER{0}/RST. Crash gone => it needs the abortive path, which tears the
                     socket down under in-flight ops rather than draining them.
      shards 1       One loop thread. Crash gone => it is cross-shard (placement, or the accept hand-off
                     bouncing sockets between shards). Crash remains => it is within a single loop
                     thread, which is a far smaller search space.
      close-after 64 Same connection count, far fewer closes. Crash gone or much rarer => rate scales
                     with CLOSES, not with traffic.
      clients 8      Fewer concurrent reconnects. Distinguishes "needs many racers" from "needs any".
      no TLS         Control. Known clean — if this ever crashes, the TLS-only claim was wrong.
      iocp+tls       Control. Known clean — confirms it is RIO's machinery, not the shared Windows base.

    Read the CONTROLS first: if either one crashes, the entry's scope claims are wrong and everything
    else here is misinterpreted.

.EXAMPLE
    .\Bisect-RioChurnCrash.ps1 -Reps 8
#>
[CmdletBinding()]
param(
    [string]$Exe,
    [int]$Reps = 8
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
if (-not $Exe) { $Exe = Join-Path $repo "SmokeTest\bin\Release\net10.0\SmokeTest.exe" }
if (-not (Test-Path $Exe)) { throw "no SmokeTest.exe at $Exe" }

$ACCESS_VIOLATION = -1073741819

# Baseline = the shipped default geometry, which is where the crash matters most.
$base = @("--rio", "--tls-schannel", "--page", "4096", "--write-buffers", "1024",
    "-s", "-c", "64", "--churn", "10", "--close-after", "4", "--sockets", "128", "--reset-close")

# NOT $args: that is a PowerShell automatic variable, and naming a parameter after it silently produces
# an empty argument list. The first run of this rig did exactly that - every variant launched SmokeTest
# with no flags, printed usage, and was scored identically as "wedge", which reads like a real (and very
# tidy) result. Confounder 9 for bench/README.md: a harness bug that makes every cell agree.
function Variant([string]$name, [string[]]$argv, [string]$means) {
    [pscustomobject]@{ Name = $name; Args = $argv; Means = $means }
}

# Each variant is the baseline with ONE substitution. Build them explicitly rather than by mutating a
# shared array — an accidental carry-over between variants would silently test the wrong thing.
$variants = @(
    Variant "baseline" $base "the shipped default; expect ~4-5/8"
    Variant "sockets 4096 (no reuse)" (@("--rio", "--tls-schannel", "--page", "4096", "--write-buffers", "1024",
            "-s", "-c", "64", "--churn", "10", "--close-after", "4", "--sockets", "4096", "--reset-close")) "gone => slot reuse / ABA"
    Variant "graceful close (no RST)" (@("--rio", "--tls-schannel", "--page", "4096", "--write-buffers", "1024",
            "-s", "-c", "64", "--churn", "10", "--close-after", "4", "--sockets", "128")) "gone => needs abortive close"
    Variant "shards 1" (@("--rio", "--tls-schannel", "--page", "4096", "--write-buffers", "1024", "-n", "1",
            "-s", "-c", "64", "--churn", "10", "--close-after", "4", "--sockets", "128", "--reset-close")) "gone => cross-shard"
    Variant "close-after 64" (@("--rio", "--tls-schannel", "--page", "4096", "--write-buffers", "1024",
            "-s", "-c", "64", "--churn", "10", "--close-after", "64", "--sockets", "128", "--reset-close")) "gone => scales with CLOSES"
    Variant "clients 8" (@("--rio", "--tls-schannel", "--page", "4096", "--write-buffers", "1024",
            "-s", "-c", "8", "--churn", "10", "--close-after", "4", "--sockets", "128", "--reset-close")) "gone => needs many racers"
    Variant "CONTROL rio plaintext" (@("--rio", "--page", "4096", "--write-buffers", "1024",
            "-s", "-c", "64", "--churn", "10", "--close-after", "4", "--sockets", "128", "--reset-close")) "must be clean"
    Variant "CONTROL iocp+tls" (@("--iocp", "--tls-schannel", "--page", "4096", "--write-buffers", "1024",
            "-s", "-c", "64", "--churn", "10", "--close-after", "4", "--sockets", "128", "--reset-close")) "must be clean"
)

$port = 16000 + (Get-Random -Minimum 0 -Maximum 400)
$log = Join-Path $env:TEMP "rio-bisect"

Write-Host ""
Write-Host "0e bisection: $($variants.Count) variants x $Reps reps" -ForegroundColor Cyan
Write-Host ""

foreach ($v in $variants) {
    $crash = 0; $wedge = 0; $pass = 0
    foreach ($rep in 1..$Reps) {
        $port++
        $p = Start-Process -FilePath $Exe -ArgumentList (@($v.Args) + @("--port", "$port")) -PassThru -NoNewWindow `
            -RedirectStandardOutput "$log.out" -RedirectStandardError "$log.err"
        $p.WaitForExit(90000) | Out-Null
        $text = Get-Content "$log.out" -Raw -ErrorAction SilentlyContinue
        $done = @($text -split "`n" | Select-String "churn: done")
        # A run that exited cleanly having never entered the churn loop did not run the scenario at all -
        # bad arguments, usage text, a rejected flag. Scoring that as a "wedge" is how the first version
        # of this rig reported eight identical rows and looked like a finding.
        if ($p.ExitCode -eq 0 -and $text -notmatch "churn:") {
            throw "HARNESS ERROR in variant '$($v.Name)': process exited 0 without entering the churn loop. Args: $($v.Args -join ' ')"
        }
        if ($p.ExitCode -eq $ACCESS_VIOLATION) { $crash++ }
        elseif ($done.Count -gt 0 -and "$($done[-1])" -match "=> PASS") { $pass++ }
        else { $wedge++ }
        Start-Sleep -Milliseconds 200
    }
    $colour = if ($crash -gt 0) { "Red" } elseif ($wedge -gt 0) { "Yellow" } else { "Green" }
    Write-Host ("  {0,-26} {1}/{2} CRASH  ({3} pass, {4} wedge)   {5}" -f `
            $v.Name, $crash, $Reps, $pass, $wedge, $v.Means) -ForegroundColor $colour
}
Write-Host ""
Write-Host "Read the two CONTROLs first: if either crashed, item 0e's scope claims are wrong." -ForegroundColor Yellow
