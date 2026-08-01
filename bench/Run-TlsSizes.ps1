<#
.SYNOPSIS
    Windows counterpart to run-tls-sizes.sh: payload-size sweep across the Windows TLS transports.

.DESCRIPTION
    Answers "how does TLS terminated in the transport (SChannel via raw SSPI) scale with payload size
    against Kestrel's own SslStream", which the fixed-size matrix in Run-Matrix.ps1 cannot show.

    A 512-byte request/response says almost nothing about a TLS stack: AES-GCM with AES-NI is ~50-100ns
    for 512 bytes against a syscall costing 1-2us, so the I/O model dominates and the cipher is rounding
    error. Sweeping payload size is what separates "the record layer is cheap" from "we never measured
    the record layer".

    Both PLAINTEXT controls are included deliberately. On Linux, omitting one produced a result where a
    TLS leg appeared to beat a plaintext leg 2:1 - which is only possible when the two are bounded by
    different things (there, the demo's Kestrel bridge). Without a plaintext control per transport there
    is nothing to separate "TLS is cheap here" from "our bridge is expensive here".

    Carries the same guards as the other harnesses: /config verified before every measurement, server and
    load generator pinned to disjoint CPU sets with both runtimes told the true core count, the first pass
    discarded as HOST warm-up (a cold machine runs at boost clocks and the transient spans a whole pass),
    reshuffled leg order per pass, and median with spread rather than a single sample.

.EXAMPLE
    .\Run-TlsSizes.ps1
.EXAMPLE
    .\Run-TlsSizes.ps1 -Sizes 4096,65536,1048576 -Repetitions 5
.EXAMPLE
    .\Run-TlsSizes.ps1 -Filter "*rio*"
#>
[CmdletBinding()]
param(
    [int[]]$Sizes = @(512, 16384, 262144),
    [string]$Duration = "8s",
    [string]$WarmupDuration = "3s",
    [int]$Connections = 64,
    # Sweepable. Kestrel legs ignore it (its transport has no shards); SocketSet legs are expanded one
    # leg per shard count, named e.g. "iocp+tls/s8". Worth sweeping because more shards is not obviously
    # better here: each shard is a dedicated loop thread competing with the ThreadPool that runs Kestrel
    # and the bridge pump, on the same pinned cores - whereas Kestrel's own transport adds no threads.
    [int[]]$Shards = @(16),
    # First pass is host warm-up and is discarded, so this is scored passes + 1. Do not drop below 3.
    [int]$Repetitions = 4,
    [int]$WarmupPasses = 1,
    [int]$Port = 5080,
    [switch]$NoPin,
    # Add the opt-in experiment legs (page-size and TLS-floor A/Bs). Off by default so the canonical
    # headline sweep is always the same set of legs.
    [switch]$Experimental,
    # Accepts SEVERAL patterns, ORed. A single wildcard cannot express "these three unrelated legs", which
    # is exactly what a targeted A/B needs — and running the full set instead just to get a subset is how
    # a 15-minute experiment becomes an hour.
    [string[]]$Filter = @("*"),
    [string]$OutDir = "$PSScriptRoot\results"
)

$ErrorActionPreference = "Stop"

# Keep the machine awake: a sweep is long enough to hit an idle-sleep timer, and a host that sleeps
# mid-run produces a partial pass whose numbers look ordinary. Lapses when this process exits.
Add-Type -Namespace Bench -Name Power -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
public static extern uint SetThreadExecutionState(uint esFlags);
'@ -ErrorAction SilentlyContinue
try { [Bench.Power]::SetThreadExecutionState(0x80000000 -bor 0x00000001) | Out-Null } catch { }

$repo = Split-Path -Parent $PSScriptRoot
$demoProj = Join-Path $repo "AspNetDemo\AspNetDemo.csproj"
$demoExe = Join-Path $repo "AspNetDemo\bin\Release\net10.0\AspNetDemo.exe"
$bombardier = Join-Path $PSScriptRoot ".tools\bombardier.exe"

if (-not (Test-Path $bombardier)) {
    New-Item -ItemType Directory -Force (Split-Path $bombardier) | Out-Null
    Invoke-WebRequest -UseBasicParsing -OutFile $bombardier `
        -Uri "https://github.com/codesenberg/bombardier/releases/download/v1.2.6/bombardier-windows-amd64.exe"
}

Write-Host "building AspNetDemo (Release) ..." -ForegroundColor Cyan
& dotnet build $demoProj -c Release -v q --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw "build failed" }

New-Item -ItemType Directory -Force $OutDir | Out-Null
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$csvPath = Join-Path $OutDir "tls-sizes-$stamp.csv"
$logDir = Join-Path $OutDir "logs-tls-sizes-$stamp"
New-Item -ItemType Directory -Force $logDir | Out-Null

$cpuCount = [Environment]::ProcessorCount
$half = [int]($cpuCount / 2)
$allMask = ([int64]1 -shl $cpuCount) - 1
$serverMask = ([int64]1 -shl $half) - 1
$clientMask = $allMask -bxor $serverMask
if ($NoPin) { $serverMask = $allMask; $clientMask = $allMask }

# Both runtimes size themselves from the CPU count seen at STARTUP, before affinity is applied. Left
# alone, the server builds a ThreadPool and Server GC heaps for every core and then runs on half.
if (-not $NoPin) { $env:DOTNET_PROCESSOR_COUNT = "$half"; $env:GOMAXPROCS = "$half" }

$script:AffinityFailures = 0
function Set-Affinity([System.Diagnostics.Process]$Process, [int64]$Mask, [string]$Who) {
    if ($NoPin) { return }
    try {
        $Process.ProcessorAffinity = [IntPtr]$Mask
        $Process.Refresh()
        if ([int64]$Process.ProcessorAffinity -ne $Mask) {
            Write-Warning "$Who affinity did not stick"; $script:AffinityFailures++
        }
    }
    catch { Write-Warning "$Who affinity failed: $($_.Exception.Message)"; $script:AffinityFailures++ }
}

# name | demo args | expected transport | expected tls
$base = @(
    @{ Name = "kestrel";     Args = @("--kestrel");          T = "kestrel-sockets"; Tls = "off" }
    @{ Name = "kestrel+tls"; Args = @("--kestrel", "--tls"); T = "kestrel-sockets"; Tls = "kestrel/sslstream" }
    @{ Name = "iocp";        Args = @("--iocp");             T = "socketset/iocp";  Tls = "off" }
    @{ Name = "iocp+tls";    Args = @("--iocp", "--tls");    T = "socketset/iocp";  Tls = "schannel (sspi)" }
    # Plaintext RIO control. Added 2026-07-27: without it, rio+tls has no same-transport baseline, and
    # this file's own opening argument is that a TLS leg with no plaintext control cannot distinguish
    # "TLS is expensive" from "our send path is expensive". That distinction is live for RIO
    # specifically - IocpShard.IssueSendPages now issues one WSASend with up to 64 WSABUFs, but
    # WindowsRioShard.IssueSend still posts RIOSend(..., &buf, 1, ...), i.e. one write page in flight
    # per connection. RIO is expected to trail IOCP at 256KB for that reason, and this leg is what makes
    # that attributable to the send path rather than to the cipher.
    @{ Name = "rio";         Args = @("--rio");              T = "socketset/rio";   Tls = "off" }
    @{ Name = "rio+tls";     Args = @("--rio", "--tls");     T = "socketset/rio";   Tls = "schannel (sspi)" }
    # http.sys — the OS's KERNEL-mode HTTP stack, added 2026-08-01. Not another transport under Kestrel:
    # a different server entirely, which is exactly why it is worth having. Every other leg here is a
    # user-mode HTTP implementation, so the whole table has been measuring variations on one design; this
    # is the outer bound, where request parsing and response framing happen in the kernel and the only
    # user-mode work is the endpoint itself.
    #
    # PLAINTEXT ONLY, and deliberately so rather than an oversight: an https prefix needs a certificate
    # bound with `netsh http add sslcert`, which needs elevation, so a TLS http.sys leg cannot be produced
    # unattended. Compare it against the plaintext legs only — reading it against a +tls leg would be
    # comparing a kernel stack doing no crypto with a user-mode stack doing crypto.
    @{ Name = "httpsys";     Args = @("--httpsys");          T = "http.sys";        Tls = "off" }
    # http.sys TLS. Needs its OWN port, which is why Port became per-leg: an `netsh add sslcert` binding
    # makes that ip:port HTTPS for every listener, so it cannot share 5080 with the plaintext legs. The
    # certificate is bound out of band by bench/Enable-HttpSysTls.ps1 (once, elevated) -- so unlike every
    # other leg here, this one has an EXTERNAL precondition, and it fails as "every request errors"
    # rather than as a startup refusal if the binding is missing. The rig therefore skips it (loudly)
    # when nothing is bound, instead of reporting a leg of zeros as though it were a measurement.
    @{ Name = "httpsys+tls"; Args = @("--httpsys", "--tls"); T = "http.sys";
       Tls = "http.sys (kernel schannel, netsh-bound cert)"; LegPort = 5443 }
)

# Opt-in legs for a specific pre-registered experiment (2026-08-01), kept OUT of the default sweep so the
# canonical table stays one stable set of legs. `Want` is an extra list of banner fragments that must ALSO
# appear in /config -- without it, --page 65536 would be gated on transport and tls only, so a page flag
# that silently did nothing would measure identically to one that worked. That is house rule 1, and this
# experiment is entirely ABOUT the page, so it is the one flag that most needs confirming.
# NOT named $experimental: PowerShell variables are CASE-INSENSITIVE, so $experimental and the
# [switch]$Experimental parameter are the SAME variable, and assigning an array to it fails with
# "Cannot convert System.Object[] to SwitchParameter" — reported against the CALLER's line, which makes
# it read as a bad argument at the call site rather than a name collision inside the script.
$experimentalLegs = @(
    # THE HYPOTHESIS: iocp+tls is last of eight legs at >=256KB because the TLS out-of-band path chunks
    # ciphertext into page-sized segments and IOCP resolves page=4096 where RIO resolves page=65536.
    # Falsifier: if the page is the mechanism, this leg moves 256KB from ~3,700 toward RIO's ~5,100+.
    # If it does not move, the page is NOT the mechanism and the difference is in the two send paths.
    @{ Name = "iocp+tls-p64k"; Args = @("--iocp", "--tls", "--page", "65536"); T = "socketset/iocp";
       Tls = "schannel (sspi)"; Want = @("page=65536") }
    # Plaintext companion: IOCP plaintext already escapes the page path via zero-copy send, so this leg
    # should NOT move much. It is here as the control that separates "the page helps the TLS path" from
    # "the page helps IOCP generally" -- without it, a TLS gain has two explanations.
    @{ Name = "iocp-p64k";     Args = @("--iocp", "--page", "65536");          T = "socketset/iocp";
       Tls = "off"; Want = @("page=65536") }
    # The TLS 1.3 floor control. Every Windows TLS number from 2026-08-01 negotiated 1.3 with no
    # same-session 1.2 comparison, leaving them discontinuous with every earlier figure. This closes that.
    @{ Name = "iocp+tls-min12"; Args = @("--iocp", "--tls", "--tls-min12");    T = "socketset/iocp";
       Tls = "schannel (sspi, min=tls12)" }
)
if ($Experimental) { $base += $experimentalLegs }

# Expand SocketSet legs across the shard sweep; the no-shard servers appear once.
# Clone rather than "+": hashtable addition throws on a duplicate key, and Name is being replaced.
$legs = @(foreach ($b in $base) {
    # Keyed on "has no shards to vary" rather than on kestrel specifically — http.sys has no shard concept
    # either, and expanding it per shard count would silently run the identical configuration N times and
    # present the repeats as a swept axis.
    if ($b.T -eq "kestrel-sockets" -or $b.T -eq "http.sys") {
        $c = $b.Clone(); $c.ShardCount = 0; $c
    }
    else {
        foreach ($s in $Shards) { $c = $b.Clone(); $c.Name = "$($b.Name)/s$s"; $c.ShardCount = $s; $c }
    }
}) | Where-Object { $n = $_.Name; @($Filter | Where-Object { $n -like $_ }).Count -gt 0 }

if (-not $legs) { throw "no legs matched filter '$($Filter -join "', '")'" }

# PREFLIGHT: drop any leg whose EXTERNAL precondition is not met, and say why.
#
# http.sys+TLS is the only leg here that depends on machine state this rig cannot create (a certificate
# bound with `netsh http add sslcert`, which needs elevation - see bench/Enable-HttpSysTls.ps1). Without
# it, http.sys starts cleanly, banners correctly, and then fails every TLS handshake - which reaches the
# results table as "no /config", i.e. a timeout rather than a diagnosis, indistinguishable from a broken
# server. Naming the real cause here is the same lesson Verify-AspNet.ps1 records.
$legs = @($legs | Where-Object {
    if (-not $_.LegPort -or $_.Tls -eq "off") { return $true }
    $bound = & netsh http show sslcert ipport=127.0.0.1:$($_.LegPort) 2>&1 | Out-String
    if ($bound -match "Certificate Hash") { return $true }
    Write-Host ("  SKIP {0}: no certificate bound to 127.0.0.1:{1}. Run ELEVATED, once:" -f $_.Name, $_.LegPort) -ForegroundColor Yellow
    Write-Host ("       $PSScriptRoot\Enable-HttpSysTls.ps1 -Port {0}" -f $_.LegPort) -ForegroundColor Yellow
    return $false
})

function Invoke-Leg($Leg, [int]$Size, [int]$Rep) {
    $scheme = if ($Leg.Tls -eq "off") { "http" } else { "https" }
    # Per-leg port override. Only http.sys+TLS uses it, because its certificate is bound to a specific
    # ip:port in the machine SSL config and cannot move to the shared port without re-running netsh.
    $legPort = if ($Leg.LegPort) { $Leg.LegPort } else { $Port }
    $url = "${scheme}://127.0.0.1:$legPort/payload?n=$Size"
    $argList = @($Leg.Args)
    # Shard count is meaningless for kestrel (its own transport) and is rejected as an unknown flag.
    if ($Leg.ShardCount -gt 0) { $argList += @("--shards", "$($Leg.ShardCount)") }
    $argList += @("--port", "$legPort")

    # Leg names carry a shard suffix like "iocp+tls/s16"; the slash would become a path separator.
    $safe = $Leg.Name -replace '[\\/:*?"<>|]', '-'
    $log = Join-Path $logDir "$safe.$Size.r$Rep"
    $proc = Start-Process -FilePath $demoExe -ArgumentList $argList -PassThru -NoNewWindow `
        -RedirectStandardOutput "$log.server.log" -RedirectStandardError "$log.server.err"
    Set-Affinity $proc $serverMask "server"

    try {
        # Wait for /config, then REFUSE to measure a leg that silently loaded something else.
        $cfg = $null
        $deadline = (Get-Date).AddSeconds(40)
        while ((Get-Date) -lt $deadline) {
            $raw = & curl.exe -sk --max-time 3 "${scheme}://127.0.0.1:$legPort/config" 2>$null
            if ($LASTEXITCODE -eq 0 -and $raw) { try { $cfg = $raw | ConvertFrom-Json; break } catch { } }
            Start-Sleep -Milliseconds 400
        }
        if ($null -eq $cfg) { Write-Host "    $($Leg.Name): no /config" -ForegroundColor Red; return $null }
        if ($cfg.config -notlike "*transport=$($Leg.T)*" -or $cfg.config -notlike "*tls=$($Leg.Tls)*") {
            Write-Host "    $($Leg.Name): MISMATCH -> $($cfg.config)" -ForegroundColor Red; return $null
        }
        # Extra banner fragments (e.g. page=65536). A leg whose whole point is a flag MUST confirm the
        # flag took: transport and tls would both match whether or not --page did anything at all.
        foreach ($w in @($Leg.Want)) {
            if ($w -and $cfg.config -notlike "*$w*") {
                Write-Host "    $($Leg.Name): banner missing '$w' -> $($cfg.config)" -ForegroundColor Red; return $null
            }
        }

        foreach ($phase in @($WarmupDuration, $Duration)) {
            $a = @("-k", "-l", "-o", "json", "-p", "r", "-c", "$Connections", "-d", $phase, "-t", "15s", $url)
            $b = Start-Process -FilePath $bombardier -ArgumentList $a -PassThru -NoNewWindow `
                -RedirectStandardOutput "$log.json" -RedirectStandardError "$log.err"
            Set-Affinity $b $clientMask "client"
            $b.WaitForExit()
        }
        $raw = Get-Content "$log.json" -Raw
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
Write-Host "TLS payload sweep: $($legs.Count) legs x $($Sizes.Count) sizes x $Repetitions passes (first $WarmupPasses discarded)" -ForegroundColor Cyan
Write-Host "  host   : $cpuCount logical processors; server=0x$($serverMask.ToString('x')) client=0x$($clientMask.ToString('x'))"
Write-Host "  load   : -c $Connections -d $Duration, keep-alive, GET /payload?n=<size>, shards=$($Shards -join ',')"
Write-Host "  csv    : $csvPath"
Write-Host ""

$results = @()
foreach ($size in $Sizes) {
    Write-Host "=== payload $size bytes ===" -ForegroundColor DarkCyan
    foreach ($rep in 1..$Repetitions) {
        # Reshuffle each pass: in a fixed order, anything that accumulates is indistinguishable from a
        # property of whichever leg runs late.
        foreach ($leg in ($legs | Sort-Object { Get-Random })) {
            $r = Invoke-Leg $leg $size $rep
            if ($null -eq $r) { continue }
            $errs = $r.others + $r.req4xx + $r.req5xx
            $mib = [math]::Round($r.rps.mean * $size / 1MB, 1)
            $results += [pscustomobject]@{
                Size = $size; Leg = $leg.Name; Rep = $rep
                Rps = [int]$r.rps.mean; MiBs = $mib
                LatP50Us = [int]$r.latency.percentiles.'50'
                LatP99Us = [int]$r.latency.percentiles.'99'
                Errors = $errs
            }
            Write-Host ("    {0,-13} {1,9:n0} rps {2,10:n1} MiB/s  p99 {3,8:n0}us  errs={4}" -f `
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
Write-Host "=== goodput MiB/s, median of $($Repetitions - $WarmupPasses) scored passes ===" -ForegroundColor Cyan
$scored = $results | Where-Object { $_.Rep -gt $WarmupPasses }
$pivot = foreach ($size in $Sizes) {
    $row = [ordered]@{ Size = $size }
    foreach ($leg in $legs) {
        $v = @($scored | Where-Object { $_.Size -eq $size -and $_.Leg -eq $leg.Name } | ForEach-Object { $_.MiBs })
        $row[$leg.Name] = if ($v.Count) { [math]::Round((Get-Median $v), 1) } else { $null }
    }
    [pscustomobject]$row
}
$pivot | Format-Table -AutoSize | Out-String | Write-Host

Write-Host "=== per-leg spread (a leg whose own range rivals the gaps is not measured, it is sampled) ===" -ForegroundColor Cyan
$scored | Group-Object Size, Leg | ForEach-Object {
    $v = @($_.Group.MiBs); $med = Get-Median $v
    $min = ($v | Measure-Object -Minimum).Minimum; $max = ($v | Measure-Object -Maximum).Maximum
    [pscustomobject]@{
        Size = $_.Group[0].Size; Leg = $_.Group[0].Leg; MedMiBs = [math]::Round($med, 1)
        SpreadPct = if ($med -gt 0) { [math]::Round(100 * ($max - $min) / $med, 1) } else { 0 }
        MedP99Us = [int](Get-Median @($_.Group.LatP99Us)); Errors = ($_.Group.Errors | Measure-Object -Sum).Sum
    }
} | Sort-Object Size, MedMiBs -Descending | Format-Table -AutoSize | Out-String | Write-Host

if ($script:AffinityFailures -gt 0) {
    Write-Host "WARNING: $($script:AffinityFailures) affinity operation(s) failed - pinning was not in force." -ForegroundColor Red
}
Write-Host "csv : $csvPath"
Write-Host ""
Write-Host "Caveat: loopback - client and server share this host, so absolute numbers are not comparable" -ForegroundColor Yellow
Write-Host "to a two-machine test. Relative comparison between legs only. Keep-alive only, so accept and"
Write-Host "TLS HANDSHAKE cost are out of scope; this measures the record layer."
