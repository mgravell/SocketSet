<#
.SYNOPSIS
    Long, hard connection-churn soak. Looks for the class of fault item 0e turned out to be: a lifetime
    bug that only appears when slots are recycled under pressure.

.DESCRIPTION
    WHY THIS EXISTS. Item 0e (an access violation in RIO+TLS under churn, on the shipped defaults) hid
    for months because **every benchmark in this repo holds keep-alive connections and measures steady
    state** - not one of them churns. The only thing that opened and closed sockets in anger was a single
    smoke cell, run once, against a ~50% fault. The fix is verified, but its bisection left one signal
    unexplained: the crash needed a TIGHT SLOT TABLE, which a stale-handle window should not care about.
    So either slot reuse merely shortened that window, or a second lifetime bug is masked. This rig is
    how you look for the second one.

    WHAT IT WATCHES, because a lifetime bug has three different faces and only one of them is loud:
      * CRASH   - exit 0xC0000005. Loud. What 0e was.
      * WEDGE   - live connections never drain to 0. What a shallow write pool did.
      * LEAK    - the run completes but the connection accounting does not balance, i.e. slots retired
                  without their teardown being observed. Quiet, and the one a short run cannot see.

    The configurations deliberately include one where the slot table is at capacity: SmokeTest runs both
    ends in one process, so N clients consume 2N slots, and `--sockets 64` x 4 shards against `-c 128` is
    exactly full. That maximises reuse pressure, which is the variable 0e's bisection implicated.

.EXAMPLE
    .\Soak-Churn.ps1
.EXAMPLE
    .\Soak-Churn.ps1 -Seconds 300 -Filter "*rio*"
#>
[CmdletBinding()]
param(
    [string]$Exe,
    [int]$Seconds = 60,
    [string]$Filter = "*"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
if (-not $Exe) { $Exe = Join-Path $repo "SmokeTest\bin\Release\net10.0\SmokeTest.exe" }
if (-not (Test-Path $Exe)) { throw "no SmokeTest.exe at $Exe" }

$ACCESS_VIOLATION = -1073741819

# name | backend+tls flags | churn shape. The shapes vary slot pressure and close frequency, which are
# the two variables 0e's bisection said mattered.
$cases = @(
    @{ Name = "rio+tls  table-full   "; Tp = @("--rio", "--tls-schannel"); Shape = @("-c", "128", "--sockets", "64", "--close-after", "1") }
    @{ Name = "rio+tls  tight        "; Tp = @("--rio", "--tls-schannel"); Shape = @("-c", "64", "--sockets", "128", "--close-after", "4") }
    @{ Name = "rio      tight        "; Tp = @("--rio"); Shape = @("-c", "64", "--sockets", "128", "--close-after", "4") }
    @{ Name = "iocp+tls table-full   "; Tp = @("--iocp", "--tls-schannel"); Shape = @("-c", "128", "--sockets", "64", "--close-after", "1") }
    @{ Name = "iocp+tls tight        "; Tp = @("--iocp", "--tls-schannel"); Shape = @("-c", "64", "--sockets", "128", "--close-after", "4") }
) | Where-Object { $_.Name -like $Filter }

$port = 17000 + (Get-Random -Minimum 0 -Maximum 400)
$log = Join-Path $env:TEMP "soak-churn"
$bad = 0

Write-Host ""
Write-Host "churn soak: $($cases.Count) cases x ${Seconds}s" -ForegroundColor Cyan
Write-Host ""

foreach ($c in $cases) {
    $port++
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process -FilePath $Exe -PassThru -NoNewWindow `
        -ArgumentList (@($c.Tp) + $c.Shape + @("-s", "--churn", "$Seconds", "--reset-close", "--port", "$port")) `
        -RedirectStandardOutput "$log.out" -RedirectStandardError "$log.err"
    $p.WaitForExit(($Seconds + 120) * 1000) | Out-Null
    $sw.Stop()

    $text = Get-Content "$log.out" -Raw -ErrorAction SilentlyContinue
    $verdict = "?"
    if ($p.ExitCode -eq $ACCESS_VIOLATION) { $verdict = "ACCESS VIOLATION"; $bad++ }
    elseif ($text -match "### UNHANDLED ###") { $verdict = "UNHANDLED EXCEPTION"; $bad++ }
    elseif ($text -match "churn: done .*connected=([\d,]+).*?=> (PASS|FAIL[^\r\n]*)") {
        $conns = $Matches[1]
        if ($Matches[2] -eq "PASS") { $verdict = "PASS  $conns connections churned" }
        else { $verdict = "WEDGE $($Matches[2])"; $bad++ }
    }
    else { $verdict = "NO RESULT (exit=$($p.ExitCode))"; $bad++ }

    $colour = if ($verdict -like "PASS*") { "Green" } else { "Red" }
    Write-Host ("  {0}  {1,6:n0}s  {2}" -f $c.Name, $sw.Elapsed.TotalSeconds, $verdict) -ForegroundColor $colour
    Start-Sleep -Seconds 2
}

Write-Host ""
if ($bad -gt 0) { Write-Host "$bad case(s) failed." -ForegroundColor Red; exit 1 }
Write-Host "All cases clean. Note this is a NECESSARY condition, not a sufficient one:" -ForegroundColor Green
Write-Host "item 0e was ~1-in-2 and still hid for months behind a suite that ran its cell once."
