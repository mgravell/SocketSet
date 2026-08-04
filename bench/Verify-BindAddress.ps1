<#
.SYNOPSIS
    Prove Listen(IPEndPoint) binds the address it was GIVEN, on the Windows backends.

.DESCRIPTION
    The Windows twin of bench/verify-bind-address.sh. Until 2026-08-04 every native backend had a literal
    `sin_addr = 0` behind a "TODO: honour the actual IP", so Listen(IPEndPoint(IPAddress.Loopback, p))
    bound INADDR_ANY and the service was reachable on every interface. Only the managed backend honoured
    the address, which is the part that makes it a trap: the same call was loopback-only when tested on
    Managed and world-reachable on the backend you shipped.

    NOTHING IN THE REPO COULD SEE IT. The smoke matrix binds IPAddress.Any (0.0.0.0 either way) and
    connects over loopback, which an Any-bound listener answers happily. A bind that ignores its argument
    and one that honours it are byte-identical under every other gate. That is house rule 2 in its
    security-relevant form.

    THE ASSERTION IS THE KERNEL'S, NOT OURS. The probe prints only the address it was ASKED for; what it
    actually bound is read back from Get-NetTCPConnection by PID. A probe reporting its own opinion of the
    bind address would have passed before the fix too.

    TWO CELLS PER BACKEND, and the second is the point:
        loopback : asked 127.0.0.1 -> the OS must report 127.0.0.1
        any      : asked 0.0.0.0   -> the OS must report 0.0.0.0   <- CONTROL
    Without the control, a build with the OPPOSITE hard-coding (always loopback) would read as correct,
    and so would a script whose parsing silently matched nothing. On Linux that control caught a real rig
    defect on its first run.

.PARAMETER Port
    Port to bind. Default 19731.
#>
[CmdletBinding()]
param([int]$Port = 19731)

$ErrorActionPreference = 'Stop'
$repo  = Split-Path -Parent $PSScriptRoot
$smoke = Join-Path $repo 'SmokeTest\bin\Release\net10.0\SmokeTest.exe'

if (-not (Test-Path $smoke)) {
    Write-Host "building SmokeTest..."
    & dotnet build (Join-Path $repo 'SmokeTest\SmokeTest.csproj') -c Release -v q --nologo -f net10.0 | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Host "build failed" -ForegroundColor Red; exit 2 }
}

$failures = 0
$cells    = 0

function Invoke-Probe {
    param([string]$Backend, [string]$Want, [string]$Label)

    $script:cells++

    # Wait for the port to be free first: a lingering listener from the previous cell is exactly how the
    # Linux version reported a FAIL against correct code on its first run.
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline -and (Get-NetTCPConnection -State Listen -LocalPort $Port -EA SilentlyContinue)) {
        Start-Sleep -Milliseconds 100
    }

    $proc = Start-Process -FilePath $smoke -PassThru -WindowStyle Hidden `
            -ArgumentList @($Backend, '-n', '1', '--bind-probe', $Want, '--port', "$Port")

    $bound = $null
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline) {
        if ($proc.HasExited) { break }
        # Match on the OWNING PID explicitly; reading whichever row came first is what made the Linux
        # version wrong (SO_REUSEPORT there let two listeners coexist on one port).
        $row = Get-NetTCPConnection -State Listen -LocalPort $Port -EA SilentlyContinue |
               Where-Object { $_.OwningProcess -eq $proc.Id } | Select-Object -First 1
        if ($row) { $bound = $row.LocalAddress; break }
        Start-Sleep -Milliseconds 200
    }

    $ok = $false; $detail = ''
    if ($null -eq $bound) {
        $detail = "no listening socket on port $Port owned by pid $($proc.Id)"
    } elseif ($bound -eq $Want) {
        $ok = $true
    } else {
        $detail = "OS reports $bound, asked for $Want"
    }

    if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -EA SilentlyContinue }
    $proc.WaitForExit(3000) | Out-Null

    if ($ok) {
        Write-Host ("  PASS  {0,-24} asked {1,-9} got {1}" -f $Label, $Want) -ForegroundColor Green
    } else {
        Write-Host ("  FAIL  {0,-24} {1}" -f $Label, $detail) -ForegroundColor Red
        $script:failures++
    }
}

Write-Host "=== Verify-BindAddress (port $Port) ==="
foreach ($be in @('--iocp', '--rio', '-m')) {
    $name = switch ($be) { '--iocp' { 'iocp' } '--rio' { 'rio' } '-m' { 'managed' } }
    Write-Host "-- $name"
    Invoke-Probe -Backend $be -Want '127.0.0.1' -Label "$name/loopback"
    Invoke-Probe -Backend $be -Want '0.0.0.0'   -Label "$name/any (control)"
}

Write-Host ''
if ($failures -eq 0) {
    Write-Host "=== Verify-BindAddress: $cells/$cells PASS ===" -ForegroundColor Green
    exit 0
} else {
    Write-Host "=== Verify-BindAddress: $failures of $cells FAILED ===" -ForegroundColor Red
    exit 1
}
