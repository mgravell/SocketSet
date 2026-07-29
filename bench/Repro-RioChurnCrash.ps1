<#
.SYNOPSIS
    Reproduce TODO item 0e: an intermittent ACCESS VIOLATION (0xC0000005) in RIO+TLS under connection
    churn, present on the SHIPPED defaults.

.DESCRIPTION
    The process dies; it does not throw. `### UNHANDLED ###` never prints, so no managed handler sees it
    - this is a fault in unsafe/native-interop code. The whole signal is the exit code, so this rig needs
    no debugger and no symbols: it runs the churn cell N times and counts how many die.

    WHY A DEDICATED RIG. The fault is intermittent - roughly 1 in 6 on the defaults - and the smoke matrix
    runs that cell ONCE. At that rate four consecutive clean runs are likely, which is precisely how this
    went unnoticed: an intermittent crash in a suite you run once per change is indistinguishable from a
    flaky harness. Anything claiming to fix it must be judged over many reps, not one.

    AND DO NOT JUDGE A FIX BY POOL DEPTH. Depth moves the frequency (a 4KB page with 128 write buffers
    showed 0/6, the shipped 4KB/1024 shows ~1/6, a 64KB page with 512 shows ~4/6) without removing the
    fault. A change that lowers the rate looks exactly like a fix and is a mask. Several depths are swept
    here for that reason: a real fix is 0 crashes across ALL of them.

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
    # page x write-buffers pairs. The FIRST is the shipped default and is the one that matters.
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
    $note = if ($page -eq 4096 -and $wb -eq 1024) { "  <-- THE SHIPPED DEFAULT" } else { "" }
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
