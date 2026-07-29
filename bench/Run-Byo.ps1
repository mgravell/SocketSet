<#
.SYNOPSIS
    Windows counterpart to run-byo.sh: does IOCP's zero-copy send buy anything, once it can actually run?

.DESCRIPTION
    IOCP's zero-copy send measured +3.5% at 16KB and nothing at 256KB (2b-result), and 2026-07-29 found
    the reason: a 256KB response through Kestrel's default ~4KB pipe blocks is 65 segments, against
    IocpConnection.MaxSendPages = 64. Confirmed directly here with SS_IOCP_STATS=1 - 40/40 responses
    declined at mean-segs=65.00, max-segs=65 - so every 256KB response fell back to copying. Off by one.

    --pipe-segment 65536 makes the same response ~5 segments, comfortably under the cap. So the first
    experiment is a flag, not a patch, and this rig runs it.

    FOUR LEGS, and the fourth is the one that keeps the reading honest:
      classic          the shipped bridge (AspNetConnection copies inbound, pumps outbound)
      byo              ctx.UsePipe, zero-copy ATTEMPTED and (at 256KB) declined - the null result
      byo-seg64k       ctx.UsePipe with 64KB pipe blocks: zero-copy actually engages
      classic-seg64k   64KB pipe blocks WITHOUT byo - the control

    Without that last leg, any byo-seg64k gain conflates zero-copy with the pipe block size itself, and
    the block size is independently worth +6-8% at >=16KB on Linux. Two changes, one number, no way to
    apportion it - which is the shape of error this file's siblings have had to retract twice.

    PRE-REGISTERED (2026-07-29, before running): if the 64-segment cap was the explanation for IOCP's
    null result, byo-seg64k should beat byo at 256KB by something like io_uring's +45.1%, and should beat
    classic-seg64k by most of that. If zero-copy engages (the rig proves it does, per leg, from the
    counter) and does NOT gain, then the cap was a real decline but not the cost, and 2b-result's "the
    bridge is structural" reading stands for IOCP.

    Every leg is gated twice: on /config reporting what was asked for, and on the SS_IOCP_STATS counter
    saying whether the fast path was TAKEN. A path that silently declines measures identically to one
    that ran and did not pay - which is exactly how the original null result came to be misread.

.EXAMPLE
    .\Run-Byo.ps1
.EXAMPLE
    .\Run-Byo.ps1 -Sizes 262144 -Repetitions 7 -Backend rio
#>
[CmdletBinding()]
param(
    [ValidateSet("iocp", "rio")]
    [string]$Backend = "iocp",
    [int[]]$Sizes = @(65536, 262144),
    [int]$Shards = 12,
    [int]$Connections = 64,
    [string]$Duration = "8s",
    [string]$WarmupDuration = "2s",
    # Pass 1 is discarded, so this is scored passes + 1. Six scored at 256KB, because three lie there:
    # the true per-cell spread is 9-17% while any three consecutive passes can span 1.2%.
    [int]$Repetitions = 7,
    [int]$Port = 5081,
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
$csvPath = Join-Path $OutDir "byo-$stamp.csv"
$logDir = Join-Path $OutDir "logs-byo-$stamp"
New-Item -ItemType Directory -Force $logDir | Out-Null

$cpuCount = [Environment]::ProcessorCount
$half = [int]($cpuCount / 2)
$allMask = ([int64]1 -shl $cpuCount) - 1
$serverMask = ([int64]1 -shl $half) - 1
$clientMask = $allMask -bxor $serverMask
if ($NoPin) { $serverMask = $allMask; $clientMask = $allMask }
if (-not $NoPin) { $env:DOTNET_PROCESSOR_COUNT = "$half"; $env:GOMAXPROCS = "$half" }

# Turn the zero-copy counter on for every leg. It is gated on this variable inside the shard, so the
# legs that do not use it pay a never-taken branch; leaving it on for all four keeps the builds identical.
$env:SS_IOCP_STATS = "1"

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

# name | extra demo args | /config fragment that MUST appear | fragment that must NOT appear
#
# The kestrel leg is the one that makes a headline number sayable. Comparing a configuration measured
# today against a vanilla-Kestrel figure from 2026-07-27 is a cross-day comparison, which this project
# has produced confident nonsense from more than once; the control has to run in the SAME session,
# reshuffled into the same passes. It takes no --shards (its transport has no shards) and is opted in.
$legs = @(
    @{ Name = "classic";        Args = @();                                     Want = "";              Deny = "byo=pipe" }
    @{ Name = "byo";            Args = @("--byo");                              Want = "byo=pipe";      Deny = "pipeseg" }
    @{ Name = "byo-seg64k";     Args = @("--byo", "--pipe-segment", "65536");   Want = "pipeseg=65536"; Deny = "" }
    @{ Name = "classic-seg64k"; Args = @("--pipe-segment", "65536");            Want = "pipeseg=65536"; Deny = "byo=pipe" }
    # The pinned pool is here because Measure-PipeMemory.ps1 (2026-07-29) found that at 2048 connections
    # --pipe-segment WITHOUT it is both the most expensive leg (3.2x classic's RSS) and the slowest. So
    # this is the configuration actually worth recommending, and it had never been throughput-tested at
    # 256KB - the memory result was measured at a 4KB payload and the throughput result at -c 64.
    @{ Name = "byo-seg64k-pin"; Args = @("--byo", "--pipe-segment", "65536", "--pipe-pinned"); Want = "pipepinned=1"; Deny = "" }
    @{ Name = "kestrel";        Args = @("--kestrel");                          Want = "transport=kestrel-sockets"; Deny = "byo=pipe"; NoShards = $true }
) | Where-Object { $_.Name -like $Filter }

if (-not $legs) { throw "no legs matched filter '$Filter'" }

function Invoke-Leg($Leg, [int]$Size, [int]$Rep) {
    # Kestrel's own transport has no shards, and --shards is rejected there as an unknown flag.
    $argList = if ($Leg.NoShards) { @($Leg.Args) + @("--port", "$Port") }
               else { @("--$Backend", "--shards", "$Shards") + @($Leg.Args) + @("--port", "$Port") }
    $log = Join-Path $logDir "$($Leg.Name).$Size.r$Rep"
    $proc = Start-Process -FilePath $demoExe -ArgumentList $argList -PassThru -NoNewWindow `
        -RedirectStandardOutput "$log.server.log" -RedirectStandardError "$log.server.err"
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
        if (-not $Leg.NoShards -and $cfg -notlike "*transport=socketset/$Backend*") { Write-Host "    $($Leg.Name): wrong transport -> $cfg" -ForegroundColor Red; return $null }
        if ($Leg.Want -and $cfg -notlike "*$($Leg.Want)*") { Write-Host "    $($Leg.Name): MISSING '$($Leg.Want)' -> $cfg" -ForegroundColor Red; return $null }
        if ($Leg.Deny -and $cfg -like "*$($Leg.Deny)*") { Write-Host "    $($Leg.Name): UNWANTED '$($Leg.Deny)' -> $cfg" -ForegroundColor Red; return $null }

        $url = "http://127.0.0.1:$Port/payload?n=$Size"
        foreach ($phase in @($WarmupDuration, $Duration)) {
            $a = @("-k", "-l", "-o", "json", "-p", "r", "-c", "$Connections", "-d", $phase, "-t", "10s", $url)
            $b = Start-Process -FilePath $bombardier -ArgumentList $a -PassThru -NoNewWindow `
                -RedirectStandardOutput "$log.json" -RedirectStandardError "$log.err"
            Set-Affinity $b $clientMask "client"
            $b.WaitForExit()
        }
        $raw = Get-Content "$log.json" -Raw
        if (-not $raw) { return $null }
        $r = ($raw | ConvertFrom-Json).result
    }
    finally {
        # Stop the server BEFORE reading the counter: the shard dumps at shutdown as well as on its 2s
        # timer, and the shutdown line is the one that covers the whole scored pass.
        try { if (-not $proc.HasExited) { $proc.Kill() } } catch { }
        try { $proc.WaitForExit(5000) | Out-Null } catch { }
        Start-Sleep -Milliseconds 500
    }

    # Rule 2 of bench/README.md, enforced by the rig: was the fast path TAKEN?
    $zc = $null
    $line = Get-Content "$log.server.err" -ErrorAction SilentlyContinue | Select-String "iocp-stats" | Select-Object -Last 1
    if ($line -and "$line" -match "zero-copy sends=([\d,]+) segments=([\d,]+).*too-fragmented=([\d,]+) \(cap=(\d+) mean-segs=([\d.]+) max-segs=([\d,]+)\).*WSASends=([\d,]+)") {
        $zc = [pscustomobject]@{
            Sends = [int64]($Matches[1] -replace ",", ""); Segs = [int64]($Matches[2] -replace ",", "")
            Declined = [int64]($Matches[3] -replace ",", ""); MeanSegs = [double]$Matches[5]
            MaxSegs = [int64]($Matches[6] -replace ",", ""); Copying = [int64]($Matches[7] -replace ",", "")
        }
    }
    return [pscustomobject]@{ Result = $r; Zc = $zc }
}

Write-Host ""
Write-Host "BYO zero-copy A/B on $Backend : $($legs.Count) legs x $($Sizes.Count) sizes x $Repetitions passes (pass 1 discarded)" -ForegroundColor Cyan
Write-Host "  shards=$Shards -c $Connections -d $Duration  server=0x$($serverMask.ToString('x')) client=0x$($clientMask.ToString('x'))"
Write-Host "  csv: $csvPath"
Write-Host ""

$results = @()
foreach ($size in $Sizes) {
    Write-Host "=== payload $size bytes ===" -ForegroundColor DarkCyan
    foreach ($rep in 1..$Repetitions) {
        foreach ($leg in ($legs | Sort-Object { Get-Random })) {
            $out = Invoke-Leg $leg $size $rep
            if ($null -eq $out -or $null -eq $out.Result) { continue }
            $r = $out.Result; $zc = $out.Zc
            $errs = $r.others + $r.req4xx + $r.req5xx
            $mib = [math]::Round($r.rps.mean * $size / 1MB, 1)
            $results += [pscustomobject]@{
                Size = $size; Leg = $leg.Name; Rep = $rep
                Rps = [int]$r.rps.mean; MiBs = $mib
                LatP50Us = [int]$r.latency.percentiles.'50'; LatP99Us = [int]$r.latency.percentiles.'99'
                Errors = $errs
                ZcSends = $(if ($zc) { $zc.Sends } else { $null })
                ZcSegs = $(if ($zc) { $zc.Segs } else { $null })
                ZcDeclined = $(if ($zc) { $zc.Declined } else { $null })
                ZcMeanSegs = $(if ($zc) { $zc.MeanSegs } else { $null })
                ZcMaxSegs = $(if ($zc) { $zc.MaxSegs } else { $null })
                CopyingSends = $(if ($zc) { $zc.Copying } else { $null })
            }
            $note = if ($zc) { "zc={0:n0}/decl={1:n0}(mean {2})" -f $zc.Sends, $zc.Declined, $zc.MeanSegs } else { "no counter" }
            Write-Host ("    {0,-15} {1,9:n0} rps {2,10:n1} MiB/s  p99 {3,7:n0}us errs={4}  {5}" -f `
                    $leg.Name, $r.rps.mean, $mib, $r.latency.percentiles.'99', $errs, $note)
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
Write-Host "=== goodput MiB/s, median of $($Repetitions - 1) scored passes, [min-max] ===" -ForegroundColor Cyan
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
            SpreadPct = [math]::Round(100 * (($v | Measure-Object -Maximum).Maximum - ($v | Measure-Object -Minimum).Minimum) / (Get-Median $v), 1)
            ZcTaken = [int64](Get-Median @($g.ZcSends)); ZcDeclined = [int64](Get-Median @($g.ZcDeclined))
            MeanSegs = (Get-Median @($g.ZcMeanSegs)); Errors = ($g.Errors | Measure-Object -Sum).Sum
        }
    }
}
$summary | Format-Table -AutoSize | Out-String | Write-Host

Write-Host "=== the comparison this rig exists for (256KB is where the prize is) ===" -ForegroundColor Cyan
foreach ($size in $Sizes) {
    $byo = $summary | Where-Object { $_.Size -eq $size -and $_.Leg -eq "byo" }
    $byoSeg = $summary | Where-Object { $_.Size -eq $size -and $_.Leg -eq "byo-seg64k" }
    $clsSeg = $summary | Where-Object { $_.Size -eq $size -and $_.Leg -eq "classic-seg64k" }
    $cls = $summary | Where-Object { $_.Size -eq $size -and $_.Leg -eq "classic" }
    $kes = $summary | Where-Object { $_.Size -eq $size -and $_.Leg -eq "kestrel" }
    foreach ($pair in @(
            @{ a = $byoSeg; b = $byo; what = "zero-copy engaging vs not (byo-seg64k vs byo)" },
            @{ a = $byoSeg; b = $clsSeg; what = "zero-copy alone, block size held equal (byo-seg64k vs classic-seg64k)" },
            @{ a = $clsSeg; b = $cls; what = "pipe block size alone (classic-seg64k vs classic)" },
            @{ a = $byoSeg; b = $cls; what = "both changes vs shipped (byo-seg64k vs classic)" },
            @{ a = $byoSeg; b = $kes; what = "best configuration vs VANILLA KESTREL, same session" },
            @{ a = $cls; b = $kes; what = "shipped bridge vs VANILLA KESTREL, same session" })) {
        if (-not $pair.a -or -not $pair.b) { continue }
        $delta = 100 * ($pair.a.MedMiBs - $pair.b.MedMiBs) / $pair.b.MedMiBs
        $disjoint = ($pair.a.Min -gt $pair.b.Max) -or ($pair.b.Min -gt $pair.a.Max)
        $verdict = if ($disjoint) { "{0:+0.0;-0.0}%" -f $delta } else { "OVERLAPPING RANGES - not a difference" }
        Write-Host ("  {0,7}B  {1,-62} {2}" -f $size, $pair.what, $verdict)
    }
}

if ($script:AffinityFailures -gt 0) {
    Write-Host "WARNING: $($script:AffinityFailures) affinity operation(s) failed - pinning was not in force." -ForegroundColor Red
}
Write-Host ""
Write-Host "csv : $csvPath"
Write-Host "ZcTaken/ZcDeclined are per-leg medians from SS_IOCP_STATS. A leg with ZcTaken=0 did not run the"
Write-Host "fast path at all, whatever its throughput says - read those columns before reading the deltas."
