<#
.SYNOPSIS
    Benchmark matrix for the AspNetDemo transports: backend x shard count x TLS, under two load shapes.

.DESCRIPTION
    Drives AspNetDemo through bombardier and emits a CSV plus a console summary.

    The matrix answers "how does vanilla Kestrel (with its own SslStream HTTPS) compare to the SocketSet
    transports", which is why the kestrel leg is always included as the control rather than being optional.

    Two load shapes, because on HTTP/1.1 they measure different things:
      keepalive - connections are held open, so accept + TLS handshake are amortised to nearly nothing.
                  This is steady-state request/response cost.
      churn     - OPT-IN (-IncludeChurn), and off by default because it exhausts the ephemeral-port pool
                  and corrupts later legs. Connection: close, so every request pays a fresh accept and
                  handshake; the DELTA between the two shapes is the per-connection cost.

    Each leg is measured -Repetitions times in a reshuffled order and reported as a median with spread,
    because a single measurement in a fixed order cannot distinguish a real difference from drift.

    Each leg is verified against /config before it is measured: a run whose reported backend or TLS mode
    does not match what was requested is recorded as MISMATCH and excluded, rather than silently
    contributing a number from the wrong transport (which is exactly how a silent fallback to managed
    sockets would otherwise be missed).

.PARAMETER Duration
    Measured run length per shape, e.g. "15s".

.PARAMETER Connections
    Concurrent connections bombardier holds.

.PARAMETER Shards
    Shard counts to sweep for the loop-thread backends. Ignored for kestrel/managed, which have none.

.PARAMETER NoPin
    Do not pin server and load generator to disjoint CPU sets. Off by default: unpinned, the aggressive
    shard counts compete with bombardier for cores and measure the scheduler rather than the transport.

.PARAMETER Filter
    Only run legs whose name matches this wildcard, e.g. "*rio*" or "*tls*".

.EXAMPLE
    .\Run-Matrix.ps1
.EXAMPLE
    .\Run-Matrix.ps1 -Duration 30s -Connections 256 -Filter "*tls*"
.EXAMPLE
    .\Run-Matrix.ps1 -Shards 8 -Repetitions 1    # quick single-pass sanity check
.EXAMPLE
    .\Run-Matrix.ps1 -IncludeChurn               # opt into churn; read the -IncludeChurn notes first
#>
[CmdletBinding()]
param(
    [string]$Duration = "15s",
    [string]$WarmupDuration = "4s",
    [int]$Connections = 128,
    [int[]]$Shards = @(4, 8, 16),
    [int]$Port = 5080,
    # Churn is bounded by REQUEST COUNT, not duration. Each request opens a fresh connection, and Windows
    # has only ~16k ephemeral ports with a multi-minute TIME_WAIT: a duration-based churn run opens tens of
    # thousands of connections, exhausts the pool, and then silently corrupts every LATER leg in the matrix
    # (measured 2026-07-26 - one 12s churn leg poisoned all 15 that followed). Keep this well under the pool.
    [int]$ChurnRequests = 3000,
    # Before each measured shape, wait for TIME_WAIT to fall below this. Guards leg N from leg N-1.
    [int]$MaxTimeWait = 3000,
    [switch]$NoPin,
    # Each leg is measured this many times, in a RESHUFFLED order per pass, and reported as a median with
    # spread. One measurement per leg in a fixed order cannot tell a real difference from drift: earlier
    # attempts showed 3x swings that tracked leg ORDER rather than backend. Spread is the honesty check --
    # if a leg's own spread rivals the between-leg differences, the matrix is measuring noise.
    [int]$Repetitions = 4,
    # The first N passes are treated as HOST warm-up and excluded from the summary (still written to the
    # CSV, flagged, so the decay stays auditable). A cold machine runs at boost clocks: measured
    # 2026-07-26, pass 1 opened at ~258k rps and decayed through itself to ~115k, while passes 2 and 3 sat
    # at a steady ~111k median and agreed with each other to within 3% per leg. Per-request warmup does
    # not fix this - the transient is thermal/frequency and spans the whole first pass.
    [int]$WarmupPasses = 1,
    # Connection churn is OPT-IN and off by default. Each request opens a fresh connection, and Windows'
    # ~16k ephemeral ports with a multi-minute TIME_WAIT mean a sustained churn run exhausts the pool and
    # corrupts every later leg. Keep-alive at -c 128 opens 128 connections total and never goes near it.
    # Cost of leaving it off: accept-path and TLS HANDSHAKE cost are out of scope; steady state exercises
    # the record layer only. That is a scope statement, not a measurement.
    [switch]$IncludeChurn,
    [string]$Filter = "*",
    [ValidateSet("fasthttp", "http1")]
    [string]$ClientKind = "fasthttp",
    [string]$OutDir = "$PSScriptRoot\results"
)

$ErrorActionPreference = "Stop"

# Keep the machine awake for the duration. A run is long enough to hit an idle-sleep timer, and a host
# that sleeps mid-matrix produces a partial pass whose numbers look ordinary. ES_CONTINUOUS is per-thread
# and lapses when this process exits, so nothing is left changed on the machine.
Add-Type -Namespace Bench -Name Power -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
public static extern uint SetThreadExecutionState(uint esFlags);
'@ -ErrorAction SilentlyContinue
try { [Bench.Power]::SetThreadExecutionState(0x80000000 -bor 0x00000001) | Out-Null } catch { } # CONTINUOUS|SYSTEM_REQUIRED

$repo = Split-Path -Parent $PSScriptRoot
$demoProj = Join-Path $repo "AspNetDemo\AspNetDemo.csproj"
$demoExe = Join-Path $repo "AspNetDemo\bin\Release\net10.0\AspNetDemo.exe"
$toolsDir = Join-Path $PSScriptRoot ".tools"
$bombardier = Join-Path $toolsDir "bombardier.exe"
$bombardierVersion = "v1.2.6"

# ---------------------------------------------------------------------------------------------------
# Tooling / build
# ---------------------------------------------------------------------------------------------------

if (-not (Test-Path $bombardier)) {
    Write-Host "bombardier not found; downloading $bombardierVersion ..." -ForegroundColor Yellow
    New-Item -ItemType Directory -Force $toolsDir | Out-Null
    $url = "https://github.com/codesenberg/bombardier/releases/download/$bombardierVersion/bombardier-windows-amd64.exe"
    Invoke-WebRequest -Uri $url -OutFile $bombardier -UseBasicParsing
}

Write-Host "building AspNetDemo (Release) ..." -ForegroundColor Cyan
# Release matters: a Debug build measures tiered-JIT warmup and disabled optimisations, not the transport.
& dotnet build $demoProj -c Release -v q --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw "build failed" }
if (-not (Test-Path $demoExe)) { throw "expected demo binary at $demoExe" }

New-Item -ItemType Directory -Force $OutDir | Out-Null
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$csvPath = Join-Path $OutDir "matrix-$stamp.csv"
$logDir = Join-Path $OutDir "logs-$stamp"
New-Item -ItemType Directory -Force $logDir | Out-Null

# ---------------------------------------------------------------------------------------------------
# CPU affinity: split the logical processors in half. Windows enumerates hyperthread siblings adjacently,
# so the lower half is the first N/2 physical cores and the upper half the rest -- a clean physical split.
# ---------------------------------------------------------------------------------------------------

$cpuCount = [Environment]::ProcessorCount
$half = [int]($cpuCount / 2)
$allMask = ([int64]1 -shl $cpuCount) - 1
$serverMask = ([int64]1 -shl $half) - 1
$clientMask = $allMask -bxor $serverMask
if ($NoPin) { $serverMask = $allMask; $clientMask = $allMask }

# Both runtimes size themselves from the processor count they see AT STARTUP, before we restrict affinity.
# Left alone, the demo builds a ThreadPool and (Server GC) heaps for all 32 CPUs and then runs on 16, and
# bombardier runs 32 Go procs on 16 -- each oversubscribed against its own affinity, which is exactly the
# kind of thing that turns into unexplained run-to-run variance. Tell them the truth up front.
if (-not $NoPin) {
    $env:DOTNET_PROCESSOR_COUNT = "$half"   # honoured by the demo (server side)
    $env:GOMAXPROCS = "$half"               # honoured by bombardier (client side)
}
else {
    Remove-Item Env:\DOTNET_PROCESSOR_COUNT -ErrorAction SilentlyContinue
    Remove-Item Env:\GOMAXPROCS -ErrorAction SilentlyContinue
}

# Sets affinity and VERIFIES it stuck. Silently-failing pinning is worse than no pinning: it produces
# numbers that look controlled but are not. Setting affinity can fail if the process has already exited,
# or if the mask does not match the system affinity mask.
$script:AffinityFailures = 0
function Set-Affinity([System.Diagnostics.Process]$Process, [int64]$Mask, [string]$Who) {
    if ($NoPin) { return }
    try {
        $Process.ProcessorAffinity = [IntPtr]$Mask
        $Process.Refresh()
        $actual = [int64]$Process.ProcessorAffinity
        if ($actual -ne $Mask) {
            Write-Warning "$Who affinity: asked 0x$($Mask.ToString('x')), got 0x$($actual.ToString('x'))"
            $script:AffinityFailures++
        }
    }
    catch {
        Write-Warning "$Who affinity failed: $($_.Exception.Message)"
        $script:AffinityFailures++
    }
}

# ---------------------------------------------------------------------------------------------------
# Legs
# ---------------------------------------------------------------------------------------------------

function New-Leg($Name, $DemoArgs, $ExpectTransport, $ExpectShards) {
    [pscustomobject]@{
        Name = $Name; DemoArgs = $DemoArgs; ExpectTransport = $ExpectTransport; ExpectShards = $ExpectShards
    }
}

$backends = @()
# Control leg first: everything else is measured relative to stock Kestrel.
$backends += New-Leg "kestrel"  @("--kestrel")  "kestrel-sockets"   $null
$backends += New-Leg "managed"  @("--managed")  "socketset/managed" $null
foreach ($s in $Shards) {
    # Only the loop-thread backends have shard threads; managed reports UsesWorkerThreads = false.
    $backends += New-Leg "iocp-s$s" @("--iocp", "--shards", "$s") "socketset/iocp" $s
    $backends += New-Leg "rio-s$s"  @("--rio",  "--shards", "$s") "socketset/rio"  $s
}

$legs = @()
foreach ($b in $backends) {
    foreach ($tls in @($false, $true)) {
        $name = if ($tls) { "$($b.Name)+tls" } else { "$($b.Name)" }
        if ($name -notlike $Filter) { continue }
        $expectTls = if (-not $tls) { "off" } elseif ($b.Name -eq "kestrel") { "kestrel/sslstream" } else { "schannel (sspi)" }
        $demoArgs = @($b.DemoArgs)
        if ($tls) { $demoArgs += "--tls" }
        $legs += [pscustomobject]@{
            Name = $name; DemoArgs = $demoArgs; Tls = $tls
            ExpectTransport = $b.ExpectTransport; ExpectShards = $b.ExpectShards; ExpectTls = $expectTls
        }
    }
}

if ($legs.Count -eq 0) { throw "no legs matched filter '$Filter'" }

# ---------------------------------------------------------------------------------------------------
# Server lifecycle
# ---------------------------------------------------------------------------------------------------

function Start-Demo($Leg, $LogPath) {
    $argList = @($Leg.DemoArgs) + @("--port", "$Port")
    $p = Start-Process -FilePath $demoExe -ArgumentList $argList -PassThru -NoNewWindow `
        -RedirectStandardOutput $LogPath -RedirectStandardError "$LogPath.err"
    Set-Affinity $p $serverMask "server"
    return $p
}

function Stop-Demo($Process) {
    if ($null -eq $Process) { return }
    try { if (-not $Process.HasExited) { $Process.Kill() } } catch { }
    try { $Process.WaitForExit(5000) | Out-Null } catch { }
}

# Poll /config until the server answers. Uses curl.exe rather than Invoke-RestMethod because Windows
# PowerShell 5.1 has no -SkipCertificateCheck and the demo certificate is self-signed.
function Wait-Config($Leg, [int]$TimeoutSeconds = 40) {
    $scheme = if ($Leg.Tls) { "https" } else { "http" }
    $url = "${scheme}://127.0.0.1:$Port/config"
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $raw = & curl.exe -sk --max-time 3 $url 2>$null
        if ($LASTEXITCODE -eq 0 -and $raw) {
            try { return ($raw | ConvertFrom-Json) } catch { }
        }
        Start-Sleep -Milliseconds 400
    }
    return $null
}

# Guard against measuring the wrong thing: a backend that silently fell back, or TLS that did not engage.
function Test-Config($Leg, $Config) {
    $problems = @()
    if ($null -eq $Config) { return @("no /config response") }
    $c = $Config.config
    if ($c -notlike "*transport=$($Leg.ExpectTransport)*") { $problems += "transport: wanted $($Leg.ExpectTransport), got '$c'" }
    if ($c -notlike "*tls=$($Leg.ExpectTls)*") { $problems += "tls: wanted $($Leg.ExpectTls), got '$c'" }
    if ($null -ne $Leg.ExpectShards -and $c -notlike "*shards=$($Leg.ExpectShards)*") { $problems += "shards: wanted $($Leg.ExpectShards)" }
    if ($Leg.Tls -and -not $Config.isHttps) { $problems += "isHttps false on a TLS leg" }
    return $problems
}

# ---------------------------------------------------------------------------------------------------
# Load generation
# ---------------------------------------------------------------------------------------------------

# Ephemeral-port gate. Windows' dynamic range is ~16k ports with a TIME_WAIT measured in minutes, so a
# churn leg leaves the pool depleted; measuring the next leg before it drains produces numbers that look
# like transport differences but are port exhaustion. Returns the observed count, or -1 on timeout.
function Wait-Ports([int]$Threshold, [int]$TimeoutSeconds = 240) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ($true) {
        $n = @(Get-NetTCPConnection -State TimeWait -ErrorAction SilentlyContinue).Count
        if ($n -lt $Threshold) { return $n }
        if ((Get-Date) -ge $deadline) { return -1 }
        Start-Sleep -Seconds 5
    }
}

function Invoke-Bombardier($Url, $Dur, [switch]$Churn, $LogPath) {
    # -p r: result only. Without it the intro banner and progress ticker precede the JSON and it will not parse.
    $a = @("-k", "-l", "-o", "json", "-p", "r", "-c", "$Connections", "-t", "5s")
    if ($Churn) { $a += @("-n", "$ChurnRequests") } else { $a += @("-d", $Dur) }

    # Client choice matters a lot: bombardier's default fasthttp client is several times faster than its
    # net/http one, and a slow client measures the CLIENT. fasthttp speaks HTTP/1.1 only, which is what we
    # want anyway (the demo pins Kestrel to HttpProtocols.Http1), so --http1 is unnecessary with it.
    # The two clients also differ in how keep-alive is disabled -- see bombardier's own -a help text.
    if ($ClientKind -eq "http1") {
        $a += "--http1"
        if ($Churn) { $a += "-a" }
    }
    elseif ($Churn) {
        # No space after the colon, deliberately: Start-Process does not quote an ArgumentList element
        # containing spaces, so "Connection: close" would split and the URL would land as a stray
        # positional arg ("unexpected <url>"). HTTP allows the optional whitespace to be omitted.
        $a += @("-H", "Connection:close")
    }
    $a += $Url
    $p = Start-Process -FilePath $bombardier -ArgumentList $a -PassThru -NoNewWindow `
        -RedirectStandardOutput $LogPath -RedirectStandardError "$LogPath.err"
    Set-Affinity $p $clientMask "client"
    $p.WaitForExit()
    $raw = Get-Content $LogPath -Raw
    if (-not $raw) { return $null }
    try { return ($raw | ConvertFrom-Json).result } catch { return $null }
}

# ---------------------------------------------------------------------------------------------------
# Run
# ---------------------------------------------------------------------------------------------------

$shapeCount = if ($IncludeChurn) { 2 } else { 1 }
Write-Host ""
Write-Host "matrix: $($legs.Count) legs x $shapeCount shape(s) x $Repetitions rep(s) = $($legs.Count * $shapeCount * $Repetitions) measurements" -ForegroundColor Cyan
Write-Host "  host      : $cpuCount logical processors"
Write-Host "  affinity  : server=0x$($serverMask.ToString('x')) client=0x$($clientMask.ToString('x'))$(if ($NoPin) { '  (UNPINNED)' })"
Write-Host "  runtimes  : DOTNET_PROCESSOR_COUNT=$($env:DOTNET_PROCESSOR_COUNT) GOMAXPROCS=$($env:GOMAXPROCS)"
Write-Host "  load      : -c $Connections -d $Duration (warmup $WarmupDuration), client=$ClientKind"
Write-Host "  churn     : $(if ($IncludeChurn) { "on (-n $ChurnRequests)" } else { 'OFF (keep-alive only)' })"
Write-Host "  output    : $csvPath"
Write-Host ""

$results = @()
foreach ($rep in 1..$Repetitions) {
    # Reshuffle every pass. A fixed order cannot separate a real difference from drift -- if leg N is
    # always measured after leg N-1, anything that accumulates (thermal, port pressure, a leaked handle)
    # is indistinguishable from a property of leg N.
    $order = $legs | Sort-Object { Get-Random }
    $i = 0
    Write-Host "--- pass $rep/$Repetitions ---" -ForegroundColor DarkCyan
    foreach ($leg in $order) {
        $i++
        $scheme = if ($leg.Tls) { "https" } else { "http" }
        $url = "${scheme}://127.0.0.1:$Port/plaintext"
        Write-Host ("[{0,2}/{1}] {2,-16}" -f $i, $order.Count, $leg.Name) -NoNewline

        $serverLog = Join-Path $logDir "$($leg.Name).r$rep.server.log"
        $proc = $null
        try {
            $proc = Start-Demo $leg $serverLog
            $config = Wait-Config $leg
            $problems = Test-Config $leg $config
            if ($problems.Count -gt 0) {
                Write-Host " MISMATCH: $($problems -join '; ')" -ForegroundColor Red
                $results += [pscustomobject]@{ Leg = $leg.Name; Shape = "-"; Rep = $rep; Status = "MISMATCH"; Detail = ($problems -join '; ') }
                continue
            }

            # Warmup: tiered JIT plus first-request paths would otherwise land in the measured window.
            Invoke-Bombardier $url $WarmupDuration -LogPath (Join-Path $logDir "$($leg.Name).r$rep.warmup.json") | Out-Null

            $shapes = @(@{ Name = "keepalive"; Churn = $false })
            if ($IncludeChurn) { $shapes += @{ Name = "churn"; Churn = $true } }

            foreach ($shape in $shapes) {
                # Never measure on a depleted ephemeral-port pool - see Wait-Ports.
                $tw = Wait-Ports $MaxTimeWait
                if ($tw -lt 0) {
                    Write-Host " [$($shape.Name): TIME_WAIT never drained]" -NoNewline -ForegroundColor Red
                    $results += [pscustomobject]@{ Leg = $leg.Name; Shape = $shape.Name; Rep = $rep; Status = "PORT-EXHAUSTED" }
                    continue
                }
                $r = Invoke-Bombardier $url $Duration -Churn:$shape.Churn -LogPath (Join-Path $logDir "$($leg.Name).r$rep.$($shape.Name).json")
                if ($null -eq $r) {
                    Write-Host " [$($shape.Name): no result]" -NoNewline -ForegroundColor Red
                    $results += [pscustomobject]@{ Leg = $leg.Name; Shape = $shape.Name; Rep = $rep; Status = "FAILED" }
                    continue
                }
                $errors = $r.others + $r.req4xx + $r.req5xx
                $results += [pscustomobject]@{
                    Leg        = $leg.Name
                    Shape      = $shape.Name
                    Rep        = $rep
                    Order      = $i    # position within this pass, so order-effects stay auditable
                    Status     = if ($errors -gt 0) { "ERRORS" } else { "ok" }
                    Rps        = [math]::Round($r.rps.mean, 0)
                    LatMeanUs  = [math]::Round($r.latency.mean, 0)
                    LatP50Us   = [math]::Round($r.latency.percentiles.'50', 0)
                    LatP99Us   = [math]::Round($r.latency.percentiles.'99', 0)
                    Req2xx     = $r.req2xx
                    Errors     = $errors
                    TimeWait   = $tw   # port-pool pressure at the start of this measurement
                    Detail     = $config.config
                }
                Write-Host (" [{0}: {1,8:n0} rps p99 {2,7:n0}us]" -f $shape.Name, $r.rps.mean, $r.latency.percentiles.'99') -NoNewline
            }
            Write-Host ""
        }
        finally {
            Stop-Demo $proc
            Start-Sleep -Seconds 2
        }
    }
}

$results | Export-Csv -Path $csvPath -NoTypeInformation -Encoding utf8

function Get-Median([double[]]$Values) {
    if ($Values.Count -eq 0) { return 0 }
    $s = $Values | Sort-Object
    $mid = [int][math]::Floor($s.Count / 2)
    if ($s.Count % 2 -eq 1) { return $s[$mid] }
    return ($s[$mid - 1] + $s[$mid]) / 2
}

# Median across passes, plus the spread. Spread is the point: a leg whose own run-to-run range rivals the
# differences BETWEEN legs has not been measured, it has been sampled from noise.
function Show-Shape($ShapeName, $Title) {
    # Warm-up passes are excluded here but retained in the CSV - see -WarmupPasses.
    $rows = $results | Where-Object { $_.Shape -eq $ShapeName -and $null -ne $_.Rps -and [int]$_.Rep -gt $WarmupPasses }
    if (-not $rows) { return }
    Write-Host "=== $Title ===" -ForegroundColor Cyan
    $summary = $rows | Group-Object Leg | ForEach-Object {
        $rps = @($_.Group.Rps)
        $med = Get-Median $rps
        $min = ($rps | Measure-Object -Minimum).Minimum
        $max = ($rps | Measure-Object -Maximum).Maximum
        [pscustomobject]@{
            Leg       = $_.Name
            MedRps    = [int]$med
            MinRps    = [int]$min
            MaxRps    = [int]$max
            SpreadPct = if ($med -gt 0) { [math]::Round(100 * ($max - $min) / $med, 1) } else { 0 }
            MedP99Us  = [int](Get-Median @($_.Group.LatP99Us))
            Errors    = ($_.Group.Errors | Measure-Object -Sum).Sum
            N         = $_.Group.Count
        }
    } | Sort-Object MedRps -Descending
    $summary | Format-Table -AutoSize | Out-String | Write-Host

    $worst = ($summary | Measure-Object SpreadPct -Maximum).Maximum
    $range = 0
    if ($summary.Count -gt 1) {
        $best = ($summary | Measure-Object MedRps -Maximum).Maximum
        $least = ($summary | Measure-Object MedRps -Minimum).Minimum
        if ($least -gt 0) { $range = [math]::Round(100 * ($best - $least) / $least, 1) }
    }
    Write-Host ("  between-leg range: {0}%   worst within-leg spread: {1}%" -f $range, $worst)
    if ($worst -ge $range) {
        Write-Host "  VERDICT: within-leg spread rivals the between-leg differences - this does NOT rank transports." -ForegroundColor Red
    }
    else {
        Write-Host "  VERDICT: between-leg differences exceed run-to-run spread - differences are likely real." -ForegroundColor Green
    }
    Write-Host ""
}

$scored = $Repetitions - $WarmupPasses
Write-Host ""
Show-Shape "keepalive" "keep-alive (steady-state request/response), median of $scored scored passes (pass 1-$WarmupPasses = host warm-up, discarded)"
Show-Shape "churn" "churn (new connection + handshake per request), median of $scored scored passes"

if ($script:AffinityFailures -gt 0) {
    Write-Host "WARNING: $($script:AffinityFailures) affinity operation(s) failed - pinning was not in force." -ForegroundColor Red
}

Write-Host "csv : $csvPath"
Write-Host "logs: $logDir"
Write-Host ""
Write-Host "Caveats -- read before quoting these numbers:" -ForegroundColor Yellow
Write-Host "  * Loopback. Client and server share the host; absolute numbers are not comparable to a"
Write-Host "    two-machine test. Treat these as RELATIVE comparisons between legs only."
Write-Host "  * TLS session resumption. bombardier's Go TLS client caches sessions, so the churn shape"
Write-Host "    measures RESUMED handshakes, not full ones. Full-handshake cost is higher than shown."
Write-Host "  * TIME_WAIT. The churn shape burns client ephemeral ports; sustained rates are capped by the"
Write-Host "    OS, not the transport. Short runs and relative comparison only."
Write-Host "  * RIO trades bulk throughput for latency by design (it services each inbound immediately"
Write-Host "    rather than coalescing), so judge it on percentiles as much as on rps."
