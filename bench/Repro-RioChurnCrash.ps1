<#
.SYNOPSIS
    Reproduce TODO item 0e: an intermittent ACCESS VIOLATION (0xC0000005) in RIO+TLS under connection
    churn, present on the default configuration.

.DESCRIPTION
    The process dies; it does not throw. `### UNHANDLED ###` never prints, so no managed handler sees it
    - this is a fault in unsafe/native-interop code. The whole signal is the exit code, so this rig needs
    no debugger and no symbols: it runs the churn cell N times and counts how many die.

    WHY A DEDICATED RIG. The fault is intermittent - roughly ONE RUN IN TWO on the defaults - and the
    smoke matrix runs that cell ONCE. Even at that rate a couple of clean runs in a row are ordinary,
    which is how it went unnoticed: an intermittent crash in a suite you run once per change is
    indistinguishable from a flaky harness. Judge anything claiming to fix it over many reps, not one.

    AND DO NOT JUDGE A FIX BY POOL DEPTH. Every depth tested crashes, except one shallow enough to WEDGE
    before it gets far enough to crash (a 4KB page with 64 write buffers wedges 8/8 and never faults -
    masking, not avoiding). An earlier version of this header claimed 128 was clean at 0/6; that was a
    pending Firewall dialog plus luck, and 128 crashes 2/8 once measured properly. Several depths are
    swept here for exactly that reason: a real fix is 0 crashes across ALL of them.

.PARAMETER Exe
    SmokeTest.exe to test. Defaults to the repo's Release build; point it at a worktree build to check
    whether a commit introduced or fixed the fault (the flags below are fully explicit, so any build runs
    the identical configuration - which is how this was shown to predate 2026-07-29).

.EXAMPLE
    .\Repro-RioChurnCrash.ps1
.EXAMPLE
    .\Repro-RioChurnCrash.ps1 -Reps 20 -Exe C:\tmp\wt\SmokeTest\bin\Release\net10.0\SmokeTest.exe
#>
[CmdletBinding()]
param(
    [string]$Exe,
    [int]$Reps = 8,
    # page x write-buffers pairs. The FIRST is the default and is the one that matters.
    [int[][]]$Configs = @(@(4096, 1024), @(4096, 512), @(65536, 512), @(65536, 256))
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
if (-not $Exe) { $Exe = Join-Path $repo "SmokeTest\bin\Release\net10.0\SmokeTest.exe" }
if (-not (Test-Path $Exe)) { throw "no SmokeTest.exe at $Exe (build it first)" }

$ACCESS_VIOLATION = -1073741819
$port = 15500 + (Get-Random -Minimum 0 -Maximum 400)
$log = Join-Path $env:TEMP "rio-churn-crash"

Write-Host ""
Write-Host "TODO 0e repro: $($Configs.Count) configs x $Reps reps  ($Exe)" -ForegroundColor Cyan
Write-Host ""

$total = 0
foreach ($cfg in $Configs) {
    $page = $cfg[0]; $wb = $cfg[1]
    $crash = 0; $wedge = 0; $pass = 0
    foreach ($rep in 1..$Reps) {
        $port++
        $p = Start-Process -FilePath $Exe -PassThru -NoNewWindow `
            -ArgumentList @("--rio", "--tls-schannel", "--page", "$page", "--write-buffers", "$wb",
            "-s", "-c", "64", "--churn", "10", "--close-after", "4", "--sockets", "128",
            "--reset-close", "--port", "$port") `
            -RedirectStandardOutput "$log.out" -RedirectStandardError "$log.err"
        $p.WaitForExit(60000) | Out-Null
        $done = @(Get-Content "$log.out" -ErrorAction SilentlyContinue | Select-String "churn: done")
        if ($p.ExitCode -eq $ACCESS_VIOLATION) { $crash++ }
        elseif ($done.Count -gt 0 -and "$($done[-1])" -match "=> PASS") { $pass++ }
        else { $wedge++ }
        Start-Sleep -Milliseconds 250
    }
    $total += $crash
    $note = if ($page -eq 4096 -and $wb -eq 1024) { "  <-- THE DEFAULT" } else { "" }
    $colour = if ($crash -gt 0) { "Red" } elseif ($wedge -gt 0) { "Yellow" } else { "Green" }
    Write-Host ("  page={0,-6} write-buffers={1,-5}  {2} pass, {3} wedge, {4} ACCESS VIOLATION{5}" -f `
            $page, $wb, $pass, $wedge, $crash, $note) -ForegroundColor $colour
}

Write-Host ""
if ($total -gt 0) {
    Write-Host "$total crash(es). Item 0e reproduces - do not treat a lower rate as a fix." -ForegroundColor Red
    exit 1
}
Write-Host "No crashes across $($Configs.Count) configs x $Reps reps. That is necessary, not sufficient:" -ForegroundColor Green
Write-Host "the fault is intermittent, so re-run with -Reps 20+ before believing it is gone."
