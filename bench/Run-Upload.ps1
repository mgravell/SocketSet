<#
.SYNOPSIS
    Large request BODIES — the inbound path, which no other rig in this repo has ever measured.

.DESCRIPTION
    WHY THIS EXISTS, and it is the same gap that let item 0e hide for months. Every benchmark here sends
    a small request and measures a large RESPONSE. `/echo` has consumed a request body since the demo was
    written and **no rig has ever POSTed to it** - so the receive path has no number at all, and the
    receive-side work (item 7: zero-copy receive + receive parking) would have been done blind.

    WHAT THE INBOUND PATH COSTS TODAY, by inspection rather than measurement, which is exactly the
    problem: `PipeIoBridge.OnReceived` copies the transport's slab into `pipe.Output.Write(data)` - one
    copy - and, when a flush is already outstanding, rents from `ArrayPool` and copies a SECOND time into
    a staging queue, because a `PipeWriter` permits only one flush in flight. Vanilla Kestrel receives
    straight into `GetMemory()` and copies neither time. So this rig should show the receive side at its
    worst under exactly the conditions the outbound rigs never create.

    GOODPUT IS COMPUTED ON THE REQUEST SIZE, not the response: the response here is one short line, so
    scoring it the usual way would measure nothing. That also means these MiB/s figures are NOT
    comparable with the payload-sweep tables, which score responses.

    PRE-REGISTERED, before the first run: classic and byo should be much CLOSER here than on the outbound
    sweep, because the zero-copy send work does not touch this direction at all - both bridges copy
    inbound identically. If byo is materially faster at upload, something other than the receive path is
    being measured. And Kestrel should lead, because it is the only one of the three not copying inbound.

    CORRECTION, on the first run: the premise of that prediction is WRONG. The two bridges do NOT share
    an inbound path - `--byo` routes receives through `PipeIoBridge.OnReceived`, `--classic` through
    `SocketSetConnection`. They are different code, so byo being faster inbound is an ordinary result and
    not a sign the rig is measuring something else. The Kestrel half of the prediction stands and is
    still worth checking. Left here rather than rewritten, because a prediction whose PREMISE was wrong
    is more useful to the next person than a tidied one.

.EXAMPLE
    .\Run-Upload.ps1
.EXAMPLE
    .\Run-Upload.ps1 -Sizes 1048576 -Repetitions 7
#>
[CmdletBinding()]
param(
    [ValidateSet("iocp", "rio")]
    [string]$Backend = "iocp",
    [int[]]$Sizes = @(4096, 65536, 1048576),
    [int]$Shards = 12,
    [int]$Connections = 64,
    [string]$Duration = "8s",
    [string]$WarmupDuration = "2s",
    [int]$Repetitions = 7,
    [int]$Port = 5083,
    [switch]$NoPin,
    [string]$Filter = "*",
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
$csvPath = Join-Path $OutDir "upload-$stamp.csv"
$bodyDir = Join-Path $env:TEMP "ss-upload-bodies"
New-Item -ItemType Directory -Force $bodyDir | Out-Null

$cpuCount = [Environment]::ProcessorCount
$half = [int]($cpuCount / 2)
$allMask = ([int64]1 -shl $cpuCount) - 1
$serverMask = ([int64]1 -shl $half) - 1
$clientMask = $allMask -bxor $serverMask
if ($NoPin) { $serverMask = $allMask; $clientMask = $allMask }
if (-not $NoPin) { $env:DOTNET_PROCESSOR_COUNT = "$half"; $env:GOMAXPROCS = "$half" }

$script:AffinityFailures = 0
function Set-Affinity([System.Diagnostics.Process]$Process, [int64]$Mask, [string]$Who) {
    if ($NoPin) { return }
    try {
        $Process.ProcessorAffinity = [IntPtr]$Mask
        $Process.Refresh()
        if ([int64]$Process.ProcessorAffinity -ne $Mask) { Write-Warning "$Who affinity did not stick"; $script:AffinityFailures++ }
    }
    catch { Write-Warning "$Who affinity failed: $($_.Exception.Message)"; $script:AffinityFailures++ }
}

# Bodies on disk once, reused by every leg and pass - generating them per run would put the client's
# allocator in the measurement.
foreach ($s in $Sizes) {
    $f = Join-Path $bodyDir "$s.bin"
    if (-not (Test-Path $f) -or (Get-Item $f).Length -ne $s) {
        [System.IO.File]::WriteAllBytes($f, (New-Object byte[] $s))
    }
}

$legs = @(
    @{ Name = "classic"; Args = @("--classic"); Want = "byo=off" }
    @{ Name = "byo";     Args = @();            Want = "byo=pipe" }   # the DEFAULT since 2026-07-31
    @{ Name = "byo-pin"; Args = @("--pipe-segment", "65536", "--pipe-pinned"); Want = "pipepinned=1" }
    @{ Name = "kestrel"; Args = @("--kestrel");  Want = "transport=kestrel-sockets"; NoShards = $true }
) | Where-Object { $_.Name -like $Filter }

function Invoke-Leg($Leg, [int]$Size, [int]$Rep) {
    $argList = if ($Leg.NoShards) { @($Leg.Args) + @("--port", "$Port") }
               else { @("--$Backend", "--shards", "$Shards") + @($Leg.Args) + @("--port", "$Port") }
    $proc = Start-Process -FilePath $demoExe -ArgumentList $argList -PassThru -NoNewWindow `
        -RedirectStandardOutput "$env:TEMP\upload.out" -RedirectStandardError "$env:TEMP\upload.err"
    Set-Affinity $proc $serverMask "server"
    try {
        $cfg = $null
        $deadline = (Get-Date).AddSeconds(40)
        while ((Get-Date) -lt $deadline) {
            $raw = & curl.exe -s --max-time 3 "http://127.0.0.1:$Port/config" 2>$null
            if ($LASTEXITCODE -eq 0 -and $raw) { try { $cfg = ($raw | ConvertFrom-Json).config; break } catch { } }
            Start-Sleep -Milliseconds 400
        }
        if (-not $cfg) { Write-Host "    $($Leg.Name): no /config" -ForegroundColor Red; return $null }
        if ($cfg -notlike "*$($Leg.Want)*") { Write-Host "    $($Leg.Name): MISSING '$($Leg.Want)' -> $cfg" -ForegroundColor Red; return $null }

        $body = Join-Path $bodyDir "$Size.bin"
        $url = "http://127.0.0.1:$Port/echo"
        foreach ($phase in @($WarmupDuration, $Duration)) {
            # -p r = print the RESULT only. Without it bombardier prepends its progress banner to stdout
            # and the JSON parse fails on "Bombarding ..." - which is why every other rig here passes it.
            $a = @("-k", "-l", "-o", "json", "-p", "r", "-m", "POST", "-f", $body,
                   "-c", "$Connections", "-d", $phase, "-t", "15s", $url)
            $b = Start-Process -FilePath $bombardier -ArgumentList $a -PassThru -NoNewWindow `
                -RedirectStandardOutput "$env:TEMP\upload.json" -RedirectStandardError "$env:TEMP\upload.jerr"
            Set-Affinity $b $clientMask "client"
            $b.WaitForExit()
        }
        $raw = Get-Content "$env:TEMP\upload.json" -Raw
        if (-not $raw) { return $null }
        return ($raw | ConvertFrom-Json).result
    }
    finally {
        try { if (-not $proc.HasExited) { $proc.Kill() } } catch { }
        try { $proc.WaitForExit(5000) | Out-Null } catch { }
        Start-Sleep -Seconds 2
    }
}

Write-Host ""
Write-Host "UPLOAD sweep (inbound path) on $Backend : $($legs.Count) legs x $($Sizes.Count) sizes x $Repetitions passes" -ForegroundColor Cyan
Write-Host "  POST /echo, goodput scored on the REQUEST body - not comparable with the response tables"
Write-Host "  csv: $csvPath"
Write-Host ""

$results = @()
foreach ($size in $Sizes) {
    Write-Host "=== request body $size bytes ===" -ForegroundColor DarkCyan
    foreach ($rep in 1..$Repetitions) {
        foreach ($leg in ($legs | Sort-Object { Get-Random })) {
            $r = Invoke-Leg $leg $size $rep
            if ($null -eq $r) { continue }
            $errs = $r.others + $r.req4xx + $r.req5xx
            $mib = [math]::Round($r.rps.mean * $size / 1MB, 1)
            $results += [pscustomobject]@{
                Size = $size; Leg = $leg.Name; Rep = $rep
                Rps = [int]$r.rps.mean; MiBs = $mib
                LatP50Us = [int]$r.latency.percentiles.'50'; LatP99Us = [int]$r.latency.percentiles.'99'
                Errors = $errs
            }
            Write-Host ("    {0,-9} {1,9:n0} rps {2,10:n1} MiB/s in  p99 {3,7:n0}us errs={4}" -f `
                    $leg.Name, $r.rps.mean, $mib, $r.latency.percentiles.'99', $errs)
        }
    }
}

$results | Export-Csv -Path $csvPath -NoTypeInformation -Encoding utf8

function Get-Median([double[]]$v) {
    if ($v.Count -eq 0) { return 0 }
    $s = $v | Sort-Object; $m = [int][math]::Floor($s.Count / 2)
    if ($s.Count % 2 -eq 1) { return $s[$m] } else { return ($s[$m - 1] + $s[$m]) / 2 }
}

Write-Host ""
Write-Host "=== inbound goodput MiB/s, median of $($Repetitions - 1) scored passes, [min-max] ===" -ForegroundColor Cyan
$scored = $results | Where-Object { $_.Rep -gt 1 }
$summary = foreach ($size in $Sizes) {
    foreach ($leg in $legs) {
        $g = @($scored | Where-Object { $_.Size -eq $size -and $_.Leg -eq $leg.Name })
        if (-not $g) { continue }
        $v = @($g.MiBs)
        [pscustomobject]@{
            Size = $size; Leg = $leg.Name
            MedMiBs = [math]::Round((Get-Median $v), 1)
            Min = ($v | Measure-Object -Minimum).Minimum; Max = ($v | Measure-Object -Maximum).Maximum
            MedP99Us = [int](Get-Median @($g.LatP99Us)); Errors = ($g.Errors | Measure-Object -Sum).Sum
        }
    }
}
$summary | Format-Table -AutoSize | Out-String | Write-Host

Write-Host "=== against the same-session Kestrel control ===" -ForegroundColor Cyan
foreach ($size in $Sizes) {
    $k = $summary | Where-Object { $_.Size -eq $size -and $_.Leg -eq "kestrel" }
    if (-not $k) { continue }
    foreach ($n in "classic", "byo", "byo-pin") {
        $x = $summary | Where-Object { $_.Size -eq $size -and $_.Leg -eq $n }
        if (-not $x) { continue }
        $delta = 100 * ($x.MedMiBs - $k.MedMiBs) / $k.MedMiBs
        $disjoint = ($x.Min -gt $k.Max) -or ($k.Min -gt $x.Max)
        $verdict = if ($disjoint) { "{0:+0.0;-0.0}%" -f $delta } else { "OVERLAPPING - not a difference" }
        Write-Host ("  {0,8}B  {1,-8} vs kestrel: {2}" -f $size, $n, $verdict)
    }
}
Write-Host ""
Write-Host "csv : $csvPath"
