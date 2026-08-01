<#
.SYNOPSIS
    Interleaved A/B of the bridge's pipe SCHEDULERS (SS_PIPE_SCHED), same binary both sides.

.DESCRIPTION
    The bridge's two pipes both default to PipeScheduler.ThreadPool at both ends, so every exchange pays a
    thread hop per direction. That is the "thread hops" term in TODO, and until 2026-08-01 only HALF of it
    had ever been tested: SS_PIPE_SCHED=inline moved the OUTBOUND reader (the SocketSet pump) only, so the
    Linux −28% result and every other "hop" number on file is about the WRITE side. The INBOUND reader —
    the one that resumes Kestrel's request pipeline when data arrives — was hard-wired to ThreadPool.

    This rig measures that missing half (`inline-read`), and the `inline-both` combination.

    WHY IT IS AN EXPERIMENT AND NOT A PROPOSED DEFAULT: an inline INBOUND reader runs Kestrel's whole
    request pipeline on the transport's loop thread, blocking that loop for every backend that owns one
    (all but managed). Kestrel runs its own IO queues for precisely this reason. So a win here is not
    directly shippable. Its value is that it UPPER-BOUNDS what removing the read-side hop could ever be
    worth, which is what decides whether an inbound half-pipe — a real fix, no loop blocking — is worth
    building. A null result deprioritises that work outright, which is just as useful.

    PRE-REGISTERED EXPECTATION: the read hop is a per-REQUEST cost, not a per-byte one — one resumption
    per request regardless of body size. So it should show up at SMALL payloads (where request rate, and
    therefore hop rate, is highest) and fade toward zero at 1 MB. If instead the gain grows with payload,
    the mechanism is not the hop and this rig has found something else. Small payloads are also exactly
    where vanilla Kestrel currently beats us, which is why they are the default sizes here.

.EXAMPLE
    .\Run-PipeSched.ps1
.EXAMPLE
    .\Run-PipeSched.ps1 -Modes off,inline-read -Sizes 512 -Repetitions 7
#>
[CmdletBinding()]
param(
    [int[]]$Sizes = @(512, 4096, 16384, 262144),
    [string[]]$Modes = @("off", "inline-read", "inline", "inline-both"),
    [ValidateSet("iocp", "rio", "managed")][string]$Backend = "iocp",
    # The bridge mode to test under. The inbound pipe exists in every mode, so the read-side hop is
    # present for all of them; byo is the default and therefore the one that matters most.
    [string[]]$DemoArgs = @("--byo"),
    [int]$Shards = 12,
    [int]$Connections = 64,
    [string]$Duration = "8s",
    [string]$WarmupDuration = "3s",
    [int]$Repetitions = 7,   # first pass discarded as host warm-up => scored = this - 1
    [int]$Port = 5120
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$demo = Join-Path $repo "AspNetDemo\bin\Release\net10.0\AspNetDemo.exe"
$bombardier = Join-Path $PSScriptRoot ".tools\bombardier.exe"
if (-not (Test-Path $bombardier)) { throw "bombardier missing: run a bench script once to fetch it" }

Write-Host "building AspNetDemo (Release) ..." -ForegroundColor Cyan
& dotnet build (Join-Path $repo "AspNetDemo\AspNetDemo.csproj") -c Release -v q --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw "build failed" }

# Split the CPUs the same way every other rig here does: server and client must not share physical cores,
# or the two contend and the measurement is of the scheduler rather than of the change.
$cpus = [Environment]::ProcessorCount
$half = [int]($cpus / 2)
$serverMask = [int64](([bigint]1 -shl $half) - 1)
$clientMask = [int64]((([bigint]1 -shl $half) - 1) -shl $half)

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$logDir = Join-Path $PSScriptRoot "results\pipesched-$stamp"
New-Item -ItemType Directory -Force $logDir | Out-Null
$csv = Join-Path $logDir "results.csv"
"size,mode,rep,rps,mib_s,p99_us,errors" | Out-File $csv -Encoding utf8

function Measure-One([string]$mode, [int]$size, [int]$rep) {
    # "kestrel" is not a scheduler mode: it is vanilla Kestrel, run in the SAME passes as the others so the
    # question this rig actually exists to answer — "does removing the read hop close the gap to Kestrel?"
    # — can be answered WITHIN a session. Reading it against a Kestrel number from another run is exactly
    # the cross-session subtraction this repo has been burned by.
    $isControl = $mode -eq "kestrel"
    # Set for the CHILD only; a stale value in this shell would silently apply to every later leg.
    if ($mode -eq "off" -or $isControl) { $env:SS_PIPE_SCHED = $null } else { $env:SS_PIPE_SCHED = $mode }
    $log = Join-Path $logDir "$mode.$size.r$rep"
    $legArgs = if ($isControl) { @("--kestrel", "--port", "$Port") }
               else { @("--$Backend") + $DemoArgs + @("--shards", "$Shards", "--port", "$Port") }
    $p = Start-Process -FilePath $demo -ArgumentList $legArgs `
        -PassThru -NoNewWindow -RedirectStandardOutput "$log.log" -RedirectStandardError "$log.err"
    try { $p.ProcessorAffinity = [IntPtr]$serverMask } catch { }
    try {
        $cfg = $null
        $deadline = (Get-Date).AddSeconds(40)
        while ((Get-Date) -lt $deadline) {
            $raw = & curl.exe -s --max-time 3 "http://127.0.0.1:$Port/config" 2>$null
            if ($LASTEXITCODE -eq 0 -and $raw) { try { $cfg = $raw | ConvertFrom-Json; break } catch { } }
            Start-Sleep -Milliseconds 400
        }
        if ($null -eq $cfg) { Write-Host "  ${mode}/${size}: no /config" -ForegroundColor Red; return $null }

        # TRUST THE BANNER. An env var not inherited by the child, or mistyped, would make two legs
        # identical and the difference would be reported as noise rather than as a broken experiment.
        $geo = [string]$cfg.geometry
        if ($isControl) {
            # The control must be REAL vanilla Kestrel: our transport publishes a geometry, Kestrel's does
            # not, so a null geometry plus the transport string is the pair that cannot both be faked by a
            # mis-parsed flag.
            if ([string]$cfg.config -notlike "*transport=kestrel-sockets*") {
                Write-Host "  kestrel/${size}: not the vanilla leg -> $($cfg.config)" -ForegroundColor Red; return $null
            }
        }
        else {
            $want = if ($mode -eq "off") { $null } else { "pipesched=$mode" }
            if ($want -and $geo -notlike "*$want*") { Write-Host "  ${mode}/${size}: banner missing '$want' -> $geo" -ForegroundColor Red; return $null }
            if (-not $want -and $geo -like "*pipesched=*") { Write-Host "  off/${size}: banner has a scheduler set -> $geo" -ForegroundColor Red; return $null }
        }

        foreach ($phase in @($WarmupDuration, $Duration)) {
            $b = Start-Process -FilePath $bombardier -PassThru -NoNewWindow `
                -ArgumentList @("-k", "-l", "-o", "json", "-p", "r", "-c", "$Connections", "-d", $phase, "-t", "15s",
                                "http://127.0.0.1:$Port/payload?n=$size") `
                -RedirectStandardOutput "$log.json" -RedirectStandardError "$log.berr"
            try { $b.ProcessorAffinity = [IntPtr]$clientMask } catch { }
            $b.WaitForExit()
        }
        $r = (Get-Content "$log.json" -Raw | ConvertFrom-Json).result
        # REFUSE a leg that errored. bombardier reports no `errors` property — failures land in
        # req4xx/req5xx/others — so reading $r.errors yields $null and prints as an empty column, which is
        # indistinguishable from "zero errors" at a glance. This rig did exactly that on its first run:
        # every leg displayed `errs=` and nothing was actually being checked.
        $errs = [int]$r.req4xx + [int]$r.req5xx + [int]$r.others
        if ($errs -gt 0 -or [int]$r.req2xx -le 0) {
            Write-Host "  ${mode}/${size}: ERRORS 4xx=$($r.req4xx) 5xx=$($r.req5xx) others=$($r.others) 2xx=$($r.req2xx)" -ForegroundColor Red
            return $null
        }
        # Percentiles are a MAP keyed by the percentile number ('99'), not properties named p99.
        Add-Member -InputObject $r -NotePropertyName ScoredP99 -NotePropertyValue ([double]$r.latency.percentiles.'99' ) -Force
        Add-Member -InputObject $r -NotePropertyName ScoredErrors -NotePropertyValue $errs -Force
        return $r
    }
    finally {
        try { if (-not $p.HasExited) { $p.Kill() } } catch { }
        try { $p.WaitForExit(5000) | Out-Null } catch { }
        $env:SS_PIPE_SCHED = $null
        Start-Sleep -Milliseconds 800
    }
}

Write-Host ""
Write-Host "pipe-scheduler A/B: $($Modes.Count) modes x $($Sizes.Count) sizes x $Repetitions passes (first discarded)" -ForegroundColor Cyan
Write-Host "  backend=$Backend $($DemoArgs -join ' ') shards=$Shards -c $Connections -d $Duration" -ForegroundColor DarkGray
Write-Host "  -> $logDir"
Write-Host ""

foreach ($size in $Sizes) {
    Write-Host "=== payload $size ===" -ForegroundColor Cyan
    for ($rep = 1; $rep -le $Repetitions; $rep++) {
        # Reshuffle the mode order every pass: a fixed order lets any slow drift in the host land on the
        # same leg every time and read as a real difference.
        foreach ($mode in ($Modes | Sort-Object { Get-Random })) {
            $r = Measure-One $mode $size $rep
            if ($null -eq $r) { continue }
            $mib = $r.rps.mean * $size / 1MB
            "$size,$mode,$rep,$($r.rps.mean),$mib,$($r.ScoredP99),$($r.ScoredErrors)" | Out-File $csv -Append -Encoding utf8
            Write-Host ("    {0,-12} {1,10:n0} rps {2,10:n1} MiB/s  p99 {3,8:n0}us  errs={4}" -f $mode, $r.rps.mean, $mib, $r.ScoredP99, $r.ScoredErrors)
        }
    }
}

Write-Host ""
Write-Host "=== goodput MiB/s, scored passes (rep 1 discarded) ===" -ForegroundColor Cyan
$rows = Import-Csv $csv | Where-Object { [int]$_.rep -ge 2 }
foreach ($size in $Sizes) {
    "--- $size ---"
    $rows | Where-Object { [int]$_.size -eq $size } | Group-Object mode | ForEach-Object {
        $v = $_.Group | ForEach-Object { [double]$_.mib_s } | Sort-Object
        if ($v.Count -eq 0) { return }
        [pscustomobject]@{
            Mode = $_.Name; Min = [math]::Round($v[0], 1); Max = [math]::Round($v[-1], 1)
            Med = [math]::Round(($v[[int]($v.Count / 2) - 1] + $v[[int]($v.Count / 2)]) / 2, 1)
        }
    } | Sort-Object Med -Descending | Format-Table -AutoSize | Out-String | Write-Host
}
Write-Host "csv: $csv"
Write-Host ""
Write-Host "Quote a delta only where the min-max ranges are DISJOINT. An inline INBOUND reader runs Kestrel"
Write-Host "on the transport loop thread, so a win here is an UPPER BOUND on removing the read hop, not a"
Write-Host "shippable default - see the header."
