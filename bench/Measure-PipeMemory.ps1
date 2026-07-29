<#
.SYNOPSIS
    What does --pipe-segment actually COST in memory on Windows? The Windows counterpart of the RSS
    half of run-pipe-opts.sh / run-recv-slab.sh.

.DESCRIPTION
    --pipe-segment 65536 is worth a great deal on IOCP - it is what lets zero-copy send engage at 256KB
    at all, measured at +117.3% - and a number that size is exactly the kind that gets promoted to a
    default without its bill being read. On LINUX the same flag costs 2.7x resident memory at 2048
    connections, which is why it is a flag there rather than a default. Nobody had measured it here.

    WHAT IS BEING MEASURED, and it is not the same slab as run-recv-slab.sh. That rig measures the
    TRANSPORT's per-socket receive buffer. This one measures the BRIDGE's pipes: Kestrel allocates pipe
    blocks per connection from a MemoryPool, so the bill scales with connections x block size, and a 16x
    bigger block is the whole question. The transport's own geometry is held fixed across every leg.

    METHOD, and the reason for each part:
      * Peak working set, sampled while the load runs, not a single reading at the end - a pool that
        fills and is then trimmed would read as free at the end.
      * An IDLE reading first, before load, so the delta attributable to connections is separable from
        the process baseline (~60-90MB of runtime here).
      * The load is a SMALL payload. A big one makes the send path dominate the footprint and buries
        the thing being measured; the pipe-block bill is a function of connection count, not size.
      * Both --byo legs report the same /config-verified transport geometry, so any difference is the
        pipe pool.

    NOTE the ephemeral-port caveat from README: this holds N keep-alive connections rather than churning
    them, so it does not need the Wait-Ports gate that Run-Matrix.ps1 has - but do not add a churn phase
    here without one.

.EXAMPLE
    .\Measure-PipeMemory.ps1
.EXAMPLE
    .\Measure-PipeMemory.ps1 -Connections 512,2048 -Duration 20s
#>
[CmdletBinding()]
param(
    [int[]]$Connections = @(64, 512, 2048),
    [string]$Duration = "15s",
    [int]$Shards = 12,
    [int]$Payload = 4096,
    [int]$Port = 5085,
    [string]$OutDir = "$PSScriptRoot\results"
)

$ErrorActionPreference = "Stop"

Add-Type -Namespace Bench -Name Power -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
public static extern uint SetThreadExecutionState(uint esFlags);
'@ -ErrorAction SilentlyContinue
try { [Bench.Power]::SetThreadExecutionState(0x80000000 -bor 0x00000001) | Out-Null } catch { }

$repo = Split-Path -Parent $PSScriptRoot
$demoProj = Join-Path $repo "AspNetDemo\AspNetDemo.csproj"
$demoExe = Join-Path $repo "AspNetDemo\bin\Release\net10.0\AspNetDemo.exe"
$bombardier = Join-Path $PSScriptRoot ".tools\bombardier.exe"
if (-not (Test-Path $bombardier)) { throw "missing $bombardier (Run-Matrix.ps1 fetches it)" }

Write-Host "building AspNetDemo (Release) ..." -ForegroundColor Cyan
& dotnet build $demoProj -c Release -v q --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw "build failed" }

New-Item -ItemType Directory -Force $OutDir | Out-Null
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$csvPath = Join-Path $OutDir "pipe-memory-$stamp.csv"

# Every leg pins the transport geometry identically, so the pipe pool is the only thing that varies.
$legs = @(
    @{ Name = "classic";        Args = @() }
    @{ Name = "byo";            Args = @("--byo") }
    @{ Name = "byo-seg64k";     Args = @("--byo", "--pipe-segment", "65536") }
    @{ Name = "byo-seg64k-pin"; Args = @("--byo", "--pipe-segment", "65536", "--pipe-pinned") }
)

function Measure-Leg($Leg, [int]$Conns) {
    $argList = @("--iocp", "--shards", "$Shards", "--port", "$Port") + $Leg.Args
    $proc = Start-Process -FilePath $demoExe -ArgumentList $argList -PassThru -NoNewWindow `
        -RedirectStandardOutput "$env:TEMP\pipemem.out" -RedirectStandardError "$env:TEMP\pipemem.err"
    try {
        $cfg = $null
        $deadline = (Get-Date).AddSeconds(40)
        while ((Get-Date) -lt $deadline) {
            $raw = & curl.exe -s --max-time 3 "http://127.0.0.1:$Port/config" 2>$null
            if ($LASTEXITCODE -eq 0 -and $raw) { try { $cfg = ($raw | ConvertFrom-Json).config; break } catch { } }
            Start-Sleep -Milliseconds 400
        }
        if (-not $cfg) { Write-Host "    $($Leg.Name): no /config" -ForegroundColor Red; return $null }
        if ($cfg -notlike "*transport=socketset/iocp*") { Write-Host "    $($Leg.Name): wrong transport" -ForegroundColor Red; return $null }

        # Settle, then take the idle baseline before any connection exists.
        Start-Sleep -Seconds 2
        $proc.Refresh()
        $idleMb = [math]::Round($proc.WorkingSet64 / 1MB, 1)

        $url = "http://127.0.0.1:$Port/payload?n=$Payload"
        $b = Start-Process -FilePath $bombardier -PassThru -NoNewWindow `
            -ArgumentList @("-k", "-o", "json", "-p", "r", "-c", "$Conns", "-d", $Duration, "-t", "10s", $url) `
            -RedirectStandardOutput "$env:TEMP\pipemem.json" -RedirectStandardError "$env:TEMP\pipemem.jerr"

        # Sample while the load runs: a pool that fills and is then trimmed reads as free at the end.
        $peak = 0
        while (-not $b.HasExited) {
            try { $proc.Refresh(); $ws = $proc.WorkingSet64; if ($ws -gt $peak) { $peak = $ws } } catch { break }
            Start-Sleep -Milliseconds 250
        }
        $b.WaitForExit()
        $proc.Refresh()
        # PeakWorkingSet64 is the OS's own high-water mark and cannot miss a spike between samples.
        try { if ($proc.PeakWorkingSet64 -gt $peak) { $peak = $proc.PeakWorkingSet64 } } catch { }

        $rps = 0
        try { $rps = [int](Get-Content "$env:TEMP\pipemem.json" -Raw | ConvertFrom-Json).result.rps.mean } catch { }
        return [pscustomobject]@{
            Leg = $Leg.Name; Conns = $Conns
            IdleMb = $idleMb; PeakMb = [math]::Round($peak / 1MB, 1)
            DeltaMb = [math]::Round($peak / 1MB - $idleMb, 1)
            KbPerConn = [math]::Round(($peak / 1KB - $idleMb * 1024) / $Conns, 1)
            Rps = $rps
        }
    }
    finally {
        try { if (-not $proc.HasExited) { $proc.Kill() } } catch { }
        try { $proc.WaitForExit(5000) | Out-Null } catch { }
        Start-Sleep -Seconds 2
    }
}

Write-Host ""
Write-Host "pipe memory: $($legs.Count) legs x $($Connections.Count) connection counts, $Payload B payload, $Shards shards" -ForegroundColor Cyan
Write-Host "  csv: $csvPath"
Write-Host ""

$results = @()
foreach ($c in $Connections) {
    Write-Host "=== $c connections ===" -ForegroundColor DarkCyan
    foreach ($leg in $legs) {
        $r = Measure-Leg $leg $c
        if ($null -eq $r) { continue }
        $results += $r
        Write-Host ("    {0,-16} idle {1,7:n1} MB  peak {2,8:n1} MB  delta {3,7:n1} MB  {4,6:n1} KB/conn  {5,8:n0} rps" -f `
                $r.Leg, $r.IdleMb, $r.PeakMb, $r.DeltaMb, $r.KbPerConn, $r.Rps)
    }
}

$results | Export-Csv -Path $csvPath -NoTypeInformation -Encoding utf8

Write-Host ""
Write-Host "=== peak working set, MB ===" -ForegroundColor Cyan
$results | Format-Table Leg, Conns, IdleMb, PeakMb, DeltaMb, KbPerConn, Rps -AutoSize | Out-String | Write-Host

Write-Host "=== the question this rig exists for: what does the 64KB pipe block cost? ===" -ForegroundColor Cyan
foreach ($c in $Connections) {
    $a = $results | Where-Object { $_.Conns -eq $c -and $_.Leg -eq "byo" }
    $b = $results | Where-Object { $_.Conns -eq $c -and $_.Leg -eq "byo-seg64k" }
    if (-not $a -or -not $b) { continue }
    $ratio = if ($a.PeakMb -gt 0) { $b.PeakMb / $a.PeakMb } else { 0 }
    Write-Host ("  {0,5} conns:  byo {1,7:n1} MB -> byo-seg64k {2,7:n1} MB   = {3:n2}x peak  (Linux measured 2.7x at 2048)" -f `
            $c, $a.PeakMb, $b.PeakMb, $ratio)
}
Write-Host ""
Write-Host "csv: $csvPath"
