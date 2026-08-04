<#
.SYNOPSIS
    Prove a listener bound to 127.0.0.1 is NOT reachable on this box's LAN address.

.DESCRIPTION
    THE OTHER HALF OF Verify-BindAddress.ps1, and the half the 2026-08-04 handover called "the one check
    that is not automated, and the one that matters most". Verify-BindAddress asks the KERNEL what address
    the socket carries (Get-NetTCPConnection by pid); this asks the NETWORK whether anyone else can reach
    it. They fail differently: a bind that took but left the socket dual-stack, or a rule elsewhere that
    re-exposes it, shows up here and nowhere else.

    NO SECOND MACHINE IS NEEDED. Connecting to this box's own LAN address from this box still carries that
    address as the destination, so a socket bound to 127.0.0.1 does not match it and the connect is
    refused. That is the same discrimination a second machine would make.

    THREE CELLS PER BACKEND, and the FIRST is the one that makes the other two mean anything:

      control     bind 0.0.0.0   -> LAN address MUST connect
      liveness    bind 127.0.0.1 -> 127.0.0.1 MUST connect
      the point   bind 127.0.0.1 -> LAN address MUST be refused

    Without the control, a host firewall blocking inbound would make EVERY backend "pass" the cell that
    matters while proving nothing whatsoever -- the exact shape of the bug being tested for. So a failed
    control is reported as INCONCLUSIVE and exits 2, not as a pass and not as a library failure. Without
    the liveness cell, a probe that crashed on startup would also read as "not reachable".

    A NOTE ON THE FIREWALL: the control binds 0.0.0.0, which is what Windows Defender Firewall prompts
    about. If no rule exists and the prompt is declined (or the session is non-interactive), inbound is
    blocked and this reports INCONCLUSIVE. That is the honest answer; it is not a reason to weaken the
    control.

.PARAMETER LanAddress
    The address to probe from outside. Defaults to the IPv4 address of the interface holding the default
    route -- NOT merely "the first non-loopback address", which on this box would pick a 169.254 link-local
    or a Hyper-V switch and test nothing.

.PARAMETER Port
    Port to bind. Default 19732 (one above Verify-BindAddress, so the two can never collide).

.PARAMETER SimulateBug
    SHOW THE GATE FAILING. A gate never observed to fail is not a gate (see REVIEW.md, where the Linux
    bind gate was checked by temporarily restoring `sin_addr = 0`). The bug this rig exists to catch was
    precisely "bind INADDR_ANY whatever you were asked for", so it can be reproduced exactly -- at the same
    observation point, through the same code path -- by asking the probe for 0.0.0.0 where the assertion
    cells ask for 127.0.0.1, with no library edit and nothing to remember to revert.

    Expected under this switch: every "loopback-only (lan)" cell FAILS and every control and liveness cell
    still passes. Anything else means the rig is measuring something other than what it claims.
#>
[CmdletBinding()]
param([string]$LanAddress, [int]$Port = 19732, [switch]$SimulateBug)

$ErrorActionPreference = 'Stop'
$repo  = Split-Path -Parent $PSScriptRoot
$smoke = Join-Path $repo 'SmokeTest\bin\Release\net10.0\SmokeTest.exe'

if (-not (Test-Path $smoke)) {
    Write-Host "building SmokeTest..."
    & dotnet build (Join-Path $repo 'SmokeTest\SmokeTest.csproj') -c Release -v q --nologo -f net10.0 | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Host "build failed" -ForegroundColor Red; exit 2 }
}

if (-not $LanAddress) {
    # The interface with the lowest-metric default route: the one a peer on the LAN would actually reach.
    $ifIndex = (Get-NetRoute -DestinationPrefix '0.0.0.0/0' -EA SilentlyContinue |
                Sort-Object RouteMetric, InterfaceMetric | Select-Object -First 1).InterfaceIndex
    if ($ifIndex) {
        $LanAddress = (Get-NetIPAddress -AddressFamily IPv4 -InterfaceIndex $ifIndex -EA SilentlyContinue |
                       Where-Object { $_.IPAddress -ne '127.0.0.1' } | Select-Object -First 1).IPAddress
    }
}
if (-not $LanAddress) {
    Write-Host "cannot determine a LAN address; pass -LanAddress explicitly" -ForegroundColor Yellow
    exit 2
}

$assertBind = if ($SimulateBug) { '0.0.0.0' } else { '127.0.0.1' }

Write-Host "=== Verify-BindReachability (lan=$LanAddress port=$Port) ==="
if ($SimulateBug) {
    Write-Host "!!! SIMULATE-BUG: the assertion cells bind $assertBind, reproducing the pre-2026-08-04" -ForegroundColor Yellow
    Write-Host "!!! INADDR_ANY hard-coding. Every 'loopback-only (lan)' cell MUST now FAIL." -ForegroundColor Yellow
}

$failures = 0; $cells = 0; $inconclusive = 0

function Test-Connect {
    param([string]$Address, [int]$TimeoutMs = 1500)
    $client = [System.Net.Sockets.TcpClient]::new()
    try {
        $iar = $client.BeginConnect($Address, $Port, $null, $null)
        if (-not $iar.AsyncWaitHandle.WaitOne($TimeoutMs)) { return $false }
        $client.EndConnect($iar)
        return $client.Connected
    } catch { return $false } finally { $client.Dispose() }
}

function Start-Probe {
    param([string]$Backend, [string]$Bind)
    # Wait for the port to clear first: a lingering listener from the previous cell would answer for it.
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline -and (Get-NetTCPConnection -State Listen -LocalPort $Port -EA SilentlyContinue)) {
        Start-Sleep -Milliseconds 100
    }
    $proc = Start-Process -FilePath $smoke -PassThru -WindowStyle Hidden `
            -ArgumentList @($Backend, '-n', '1', '--bind-probe', $Bind, '--port', "$Port")
    # Wait until the kernel actually shows it listening, rather than sleeping a guessed interval.
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline) {
        if ($proc.HasExited) { break }
        if (Get-NetTCPConnection -State Listen -LocalPort $Port -OwningProcess $proc.Id -EA SilentlyContinue) { break }
        Start-Sleep -Milliseconds 150
    }
    return $proc
}

function Stop-Probe {
    param($Proc)
    if ($Proc -and -not $Proc.HasExited) { Stop-Process -Id $Proc.Id -Force -EA SilentlyContinue }
    if ($Proc) { $Proc.WaitForExit(3000) | Out-Null }
}

function Write-Cell {
    param([string]$Label, [bool]$Ok, [string]$Detail)
    $script:cells++
    if ($Ok) { Write-Host ("  PASS  {0,-34} {1}" -f $Label, $Detail) -ForegroundColor Green }
    else     { Write-Host ("  FAIL  {0,-34} {1}" -f $Label, $Detail) -ForegroundColor Red; $script:failures++ }
}

foreach ($be in @('--iocp', '--rio', '-m')) {
    $name = switch ($be) { '--iocp' { 'iocp' } '--rio' { 'rio' } '-m' { 'managed' } }
    Write-Host "-- $name"

    # 1. CONTROL: bound to everything, so the LAN address MUST answer. If it does not, nothing below
    #    this line can be believed and the backend is skipped rather than scored.
    $p = Start-Probe -Backend $be -Bind '0.0.0.0'
    $lanOnAny = Test-Connect -Address $LanAddress
    Stop-Probe -Proc $p
    if (-not $lanOnAny) {
        Write-Host ("  INCONCLUSIVE  {0,-26} bound 0.0.0.0 and {1}:{2} still did not answer -- host firewall or no route; the refusal cell below would prove nothing" -f "$name/control", $LanAddress, $Port) -ForegroundColor Yellow
        $inconclusive++
        continue
    }
    Write-Cell -Label "$name/control (any -> lan)" -Ok $true -Detail "$LanAddress`:$Port answered, so the probe below is discriminating"

    # 2 and 3 share one listener: liveness and the assertion must be made of the SAME process, or a probe
    #    that died between them reads as a pass.
    $p = Start-Probe -Backend $be -Bind $assertBind
    $loopOk = Test-Connect -Address '127.0.0.1'
    $lanOk  = Test-Connect -Address $LanAddress
    Stop-Probe -Proc $p

    Write-Cell -Label "$name/liveness (loopback)" -Ok $loopOk -Detail $(if ($loopOk) { "127.0.0.1:$Port answered" } else { "127.0.0.1:$Port did not answer -- probe never listened; the cell below is meaningless" })
    Write-Cell -Label "$name/loopback-only (lan)" -Ok (-not $lanOk) -Detail $(if ($lanOk) { "REACHABLE on $LanAddress while bound to $assertBind -- the bind did not take" } else { "refused on $LanAddress, as it must be" })
}

Write-Host ''
if ($inconclusive -gt 0 -and $failures -eq 0) {
    Write-Host "=== Verify-BindReachability: INCONCLUSIVE ($inconclusive backend(s) could not be reached even bound to 0.0.0.0) ===" -ForegroundColor Yellow
    exit 2
}
if ($failures -eq 0) {
    Write-Host "=== Verify-BindReachability: $cells/$cells PASS ===" -ForegroundColor Green
    exit 0
}
Write-Host "=== Verify-BindReachability: $failures of $cells FAILED ===" -ForegroundColor Red
exit 1
