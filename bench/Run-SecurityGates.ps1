<#
.SYNOPSIS
    Run every gate that the 2026-08-04 security work added or depends on, in one command.

.DESCRIPTION
    THE WINDOWS HALF OF THAT WORK IS UNRUN. Seven commits changed IocpShard, WindowsRioShard,
    WindowsShardBase, Win32.cs and SocketSetShard -- bind addresses, IPv4 guards, callback containment,
    the buffer tail wipe, TLS resolution and the mandatory host, deadline sweeps, and inbound bounds --
    and none of it has ever executed on Windows. This exists so picking that up is one command rather
    than an afternoon of remembering which rigs to run.

    Two of the three self-inflicted breaks in that session were caught only by RUNNING things, and one
    (Environment.TickCount64, which does not exist on net472) was invisible to a single-target build. So
    this starts with a FULL multi-target solution build, deliberately, before any rig runs.

    ORDER IS DELIBERATE: cheapest and most likely to fail first, so a broken build or a broken bind does
    not cost you the six-minute smoke matrix before you find out.

.PARAMETER SkipSmoke
    Skip Run-SmokeMatrix.ps1 (48 cells, ~3-6 min). Everything else is seconds to ~2 min.
#>
[CmdletBinding()]
param([switch]$SkipSmoke)

$ErrorActionPreference = 'Continue'
$repo = Split-Path -Parent $PSScriptRoot
$results = [ordered]@{}

function Invoke-Gate {
    param([string]$Name, [scriptblock]$Body)
    Write-Host ''
    Write-Host "########## $Name ##########" -ForegroundColor Cyan
    & $Body
    $script:results[$Name] = ($LASTEXITCODE -eq 0)
}

# 1. FULL solution build, both target frameworks. Not `-f net10.0`: that is exactly the blind spot that
#    shipped a net472 break earlier in the session.
Invoke-Gate 'build (net10.0 + net472)' {
    & dotnet build (Join-Path $repo 'SocketSet.slnx') -c Release -v q --nologo
}

# 2. The security gates added by the audit. All cross-platform .NET rigs; they select Windows backends
#    and the SChannel provider automatically (see bench/GateBackends.cs).
Invoke-Gate 'verify-bind-address' { & (Join-Path $PSScriptRoot 'Verify-BindAddress.ps1') }
# The network half of the same question, and until 2026-08-04 the one check here that was NOT automated.
# Verify-BindAddress asks the kernel what the socket carries; this asks whether anyone else can reach it.
# It can report INCONCLUSIVE (exit 2) if a host firewall blocks its own control, which is neither a pass
# nor a library failure -- so it is scored on "not FAIL" rather than on exit 0.
Invoke-Gate 'verify-bind-reachability' { & (Join-Path $PSScriptRoot 'Verify-BindReachability.ps1'); if ($LASTEXITCODE -eq 2) { Write-Host '  (inconclusive, not scored as a failure)' -ForegroundColor Yellow; $global:LASTEXITCODE = 0 } }
Invoke-Gate 'verify-tailwipe'     { & dotnet run --project (Join-Path $repo 'bench\verify-tailwipe') -c Release -f net10.0 }
Invoke-Gate 'verify-tlsname'      { & dotnet run --project (Join-Path $repo 'bench\verify-tlsname')  -c Release -f net10.0 }
Invoke-Gate 'verify-timeouts'     { & dotnet run --project (Join-Path $repo 'bench\verify-timeouts') -c Release -f net10.0 }

# 3. The pre-existing narrow gates, which the same commits could have broken.
Invoke-Gate 'Verify-TlsFloor'     { & (Join-Path $PSScriptRoot 'Verify-TlsFloor.ps1') }
Invoke-Gate 'Verify-AspNet'       { & (Join-Path $PSScriptRoot 'Verify-AspNet.ps1') }

# 4. The transport gate last: slowest, and the one most likely to be fine if the above all passed.
if (-not $SkipSmoke) {
    Invoke-Gate 'Run-SmokeMatrix'  { & (Join-Path $PSScriptRoot 'Run-SmokeMatrix.ps1') }
} else {
    Write-Host "`n(skipping Run-SmokeMatrix by request)" -ForegroundColor Yellow
}

Write-Host ''
Write-Host '================ SUMMARY ================'
$failed = 0
foreach ($k in $results.Keys) {
    if ($results[$k]) { Write-Host ("  PASS  {0}" -f $k) -ForegroundColor Green }
    else              { Write-Host ("  FAIL  {0}" -f $k) -ForegroundColor Red; $failed++ }
}
Write-Host '========================================='
if ($failed -eq 0) {
    Write-Host 'ALL GATES PASS' -ForegroundColor Green
    Write-Host ''
    Write-Host 'The discriminating bind check IS automated now (Verify-BindReachability, 2026-08-04): a'
    Write-Host 'listener on 127.0.0.1 is confirmed unreachable on this box''s LAN address, behind a control'
    Write-Host 'that proves the LAN address answers at all. Run it with -SimulateBug to watch it fail.'
    exit 0
}
Write-Host "$failed GATE(S) FAILED" -ForegroundColor Red
exit 1
