<#
.SYNOPSIS
    Runtime correctness gate for the ASP.NET bridge: /config banner, byte-exact /payload and /echo,
    /stats counters, across backend x bridge-mode x TLS.

.DESCRIPTION
    Run-SmokeMatrix.ps1 gates the TRANSPORT. Nothing gated the BRIDGE, which is why the
    SocketSet.AspNetCore library extraction (branch package-aspnetcore-lib) could be "builds 0/0" and
    still completely unverified at runtime — a build says nothing about whether UseSocketSet actually
    replaces Kestrel's transport, whether the options map onto the bridge, or whether the response
    bytes survive the trip.

    Written to be run on BOTH SIDES of that extraction with identical cells, so "the refactor preserves
    behaviour" is a measured claim rather than an inspection. It is also the gate for the second open
    Windows item: --half-pipe is merged to main and UNTESTED on IOCP/RIO ("uses only cross-platform
    Connection.Send, so it SHOULD work"). Every mode is a cell here precisely so SHOULD becomes DOES.

    House rule 1 (trust the banner, not the flag) is the reason /config is gated FIRST and a cell fails
    outright when the banner does not name the backend and mode that were asked for: a flag that parses
    and is ignored produces byte-exact payloads too, and would pass every other check in this file
    while measuring the wrong thing entirely.

    House rule 2 (confirm the path was TAKEN) is why /stats is gated on accepts > 0 rather than merely
    being reachable — a transport that never accepted anything cannot have served the payloads, so a
    zero there means the responses came from somewhere other than the leg under test.

.EXAMPLE
    .\Verify-AspNet.ps1
.EXAMPLE
    .\Verify-AspNet.ps1 -Filter "*half-pipe*" -KeepLogs
#>
[CmdletBinding()]
param(
    # Cell name filter. Cell names are "<backend>/<mode><+tls>".
    [string]$Filter = "*",
    [int]$FirstPort = 5300,
    # Per-cell ceiling. A bridge that wedges must be reported, not waited on.
    [int]$TimeoutSec = 120,
    # Where to write the machine-readable result, for a before/after diff across the extraction.
    [string]$Tag = "",
    [switch]$KeepLogs
)

$ErrorActionPreference = "Stop"

$repo = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $repo "AspNetDemo\AspNetDemo.csproj"
$exe = Join-Path $repo "AspNetDemo\bin\Release\net10.0\AspNetDemo.exe"

Write-Host "building AspNetDemo (Release) ..." -ForegroundColor Cyan
& dotnet build $proj -c Release -v q --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw "build failed" }
if (-not (Test-Path $exe)) { throw "no AspNetDemo.exe at $exe" }

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$logDir = Join-Path $PSScriptRoot "results\aspnet-$stamp$(if ($Tag) { "-$Tag" })"
New-Item -ItemType Directory -Force $logDir | Out-Null

# --- the matrix ------------------------------------------------------------------------------------
# Managed is included for the same reason the smoke matrix includes it: it is the path that runs wherever
# a Windows backend is unavailable, and it shares the bridge under test.
$backends = @(
    @{ Name = "iocp";    Args = @("--iocp");    Banner = "transport=socketset/iocp" }
    @{ Name = "rio";     Args = @("--rio");     Banner = "transport=socketset/rio" }
    @{ Name = "managed"; Args = @("--managed"); Banner = "transport=socketset/managed" }
)
# Every bridge mode, because the extraction moved the mode SELECTION into the library (DemoConfig.ApplyTo
# -> SocketSetBridgeMode) and a mis-mapped enum would silently run one mode while the banner named it
# correctly from the demo's own flags. Gating the banner catches a flag that did nothing; only running
# all three catches a mode that ran as a different one.
$modes = @(
    @{ Name = "byo";       Args = @("--byo");       Banner = "byo=pipe" }
    @{ Name = "classic";   Args = @("--classic");   Banner = "byo=off" }
    @{ Name = "half-pipe"; Args = @("--half-pipe"); Banner = "half-pipe=1" }
)
$tlsModes = @(
    @{ Suffix = "";     Args = @();      Banner = "tls=off";             Scheme = "http" }
    @{ Suffix = "+tls"; Args = @("--tls"); Banner = "tls=schannel (sspi)"; Scheme = "https" }
)

# Sizes bracket the interesting boundaries rather than sampling evenly: 1 byte (does a single-byte body
# survive a bridge built around block-sized segments), 4095/4097 (either side of the default ~4KB pipe
# block, where an off-by-one in segment accounting lives), 65536 (the RIO page), 8MB (the io_uring
# IovMax prefix boundary was at 4MB, so the top of the demo's clamp is the far side of it).
$payloadSizes = @(1, 2, 100, 1024, 4095, 4096, 4097, 8192, 65536, 100000, 1048576, 4194304, 8388608)
# Inbound. Smaller set — the receive path has no prefix/segment cliff of its own, but a 1MB POST does
# cross the pipe-flush backpressure path that the classic bridge stages through.
$echoSizes = @(1, 4096, 1048576)

# --- one HttpClient per cell, because the point is to exercise the transport, not the client ---------
Add-Type -AssemblyName System.Net.Http -ErrorAction SilentlyContinue
function New-Client([int]$TimeoutSec) {
    $h = [System.Net.Http.HttpClientHandler]::new()
    # The demo's certificate is a throwaway self-signed one for localhost (DemoCertificate), so the
    # client MUST skip verification — this is the scripted equivalent of the README's `curl -k`.
    #
    # NOT a PowerShell scriptblock. A scriptblock assigned here fails at handshake time with "There is no
    # Runspace available to run scripts in this thread" — the callback is invoked on a TLS worker thread
    # that has no PowerShell runspace attached — and the resulting HttpRequestException reads as a TLS
    # failure, so it looks exactly like a broken SChannel provider. It cost a false FAIL on every TLS cell
    # here once. The framework's static validator is a plain delegate and needs no runspace.
    $h.ServerCertificateCustomValidationCallback = [System.Net.Http.HttpClientHandler]::DangerousAcceptAnyServerCertificateValidator
    $c = [System.Net.Http.HttpClient]::new($h)
    $c.Timeout = [timespan]::FromSeconds($TimeoutSec)
    # Pin HTTP/1.1 to match Kestrel's listener, which the demo pins so the legs stay comparable.
    $c.DefaultRequestVersion = [version]"1.1"
    $c.DefaultVersionPolicy = [System.Net.Http.HttpVersionPolicy]::RequestVersionExact
    return $c
}

# Byte-exactness is checked in .NET, not in the PowerShell pipeline: an 8MB body through
# `$bytes | Where-Object` takes minutes and would make the rig's own cost the thing being measured.
$expectedCache = @{}
function Test-Payload([byte[]]$Got, [int]$Size) {
    if ($null -eq $Got) { return "null body" }
    if ($Got.Length -ne $Size) { return "length $($Got.Length) != $Size" }
    if (-not $expectedCache.ContainsKey($Size)) {
        $e = [byte[]]::new($Size)
        [System.Array]::Fill($e, [byte]0x78)  # 'x', what /payload fills with
        $expectedCache[$Size] = $e
    }
    if (-not [System.Linq.Enumerable]::SequenceEqual([byte[]]$Got, [byte[]]$expectedCache[$Size])) {
        return "content mismatch at $Size"
    }
    return $null
}

$cells = @(foreach ($b in $backends) {
        foreach ($m in $modes) {
            foreach ($t in $tlsModes) {
                [pscustomobject]@{
                    Name    = "$($b.Name)/$($m.Name)$($t.Suffix)"
                    Args    = @($b.Args) + @($m.Args) + @($t.Args)
                    Expect  = @($b.Banner, $m.Banner, $t.Banner)
                    Scheme  = $t.Scheme
                }
            }
        }
    }) | Where-Object { $_.Name -like $Filter }

if (-not $cells) { throw "no cells matched filter '$Filter'" }

Write-Host ""
Write-Host "aspnet verify: $($cells.Count) cells -> $logDir" -ForegroundColor Cyan
Write-Host ""

$port = $FirstPort
$results = @()
foreach ($cell in $cells) {
    $port++
    $safe = $cell.Name -replace '[\\/:*?"<>|+]', '-'
    $out = Join-Path $logDir "$safe.log"
    $err = Join-Path $logDir "$safe.err"
    $argList = @($cell.Args) + @("--port", "$port")

    $sw = [Diagnostics.Stopwatch]::StartNew()
    $ok = $true; $detail = ""; $client = $null; $p = $null
    try {
        $p = Start-Process -FilePath $exe -ArgumentList $argList -PassThru -NoNewWindow `
            -RedirectStandardOutput $out -RedirectStandardError $err

        $client = New-Client $TimeoutSec
        $base = "$($cell.Scheme)://127.0.0.1:$port"

        # --- wait for the listener, and fail LOUDLY if the process died instead of binding -----------
        # Keep the LAST connect failure. "No /config after 20s" is not a diagnosis — it cannot distinguish
        # a server that never bound from a client that cannot speak to it, and reporting the bare timeout
        # sent a rig bug (see New-Client) to the results table as a transport failure.
        $cfgJson = $null; $lastEx = "no attempt made"
        for ($i = 0; $i -lt 200; $i++) {
            if ($p.HasExited) { break }
            try { $cfgJson = $client.GetStringAsync("$base/config").GetAwaiter().GetResult(); break }
            catch {
                $lastEx = $_.Exception.Message
                $inner = $_.Exception.InnerException
                while ($inner) { $lastEx = "$lastEx <- $($inner.Message)"; $inner = $inner.InnerException }
                Start-Sleep -Milliseconds 100
            }
        }
        if ($p.HasExited) {
            $msg = ((Get-Content $out -Raw -EA SilentlyContinue) + (Get-Content $err -Raw -EA SilentlyContinue))
            $first = ($msg -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -First 1)
            throw "server exited (code $($p.ExitCode)): $first"
        }
        if (-not $cfgJson) { throw "no /config after 20s -- last error: $lastEx" }

        # --- gate 1: the BANNER, not the flag -------------------------------------------------------
        $cfg = $cfgJson | ConvertFrom-Json
        foreach ($want in $cell.Expect) {
            if ($cfg.config -notlike "*$want*") { throw "banner missing '$want' -- got: $($cfg.config)" }
        }
        # The geometry is what the BACKEND resolved, and a 0 there means a read site missed the
        # "backend chooses" sentinel — the exact failure the geometry rework could reintroduce, and one
        # that is invisible in throughput.
        if (-not $cfg.geometry) { throw "no resolved geometry (transport never bound?)" }
        if ($cfg.geometry -match '=0(\s|$)') { throw "geometry has a 0: $($cfg.geometry)" }

        # --- gate 2: byte-exact outbound ------------------------------------------------------------
        foreach ($n in $payloadSizes) {
            $bytes = $client.GetByteArrayAsync("$base/payload?n=$n").GetAwaiter().GetResult()
            $bad = Test-Payload $bytes $n
            if ($bad) { throw "/payload?n=$n : $bad" }
        }

        # --- gate 3: byte-exact inbound -------------------------------------------------------------
        foreach ($n in $echoSizes) {
            $body = [byte[]]::new($n)
            [System.Array]::Fill($body, [byte]0x79)  # 'y' — distinct from the outbound fill
            $content = [System.Net.Http.ByteArrayContent]::new($body)
            $resp = $client.PostAsync("$base/echo", $content).GetAwaiter().GetResult()
            if (-not $resp.IsSuccessStatusCode) { throw "/echo $n : HTTP $([int]$resp.StatusCode)" }
            $text = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            if ($text -notmatch "echoed (\d+) bytes") { throw "/echo $n : no echoed line ('$text')" }
            if ([int]$Matches[1] -ne $n) { throw "/echo $n : server saw $($Matches[1]) bytes" }
        }

        # --- gate 4: the transport actually served this ---------------------------------------------
        $stats = ($client.GetStringAsync("$base/stats").GetAwaiter().GetResult()) | ConvertFrom-Json
        if ($stats.accepts -le 0) { throw "stats.accepts = $($stats.accepts) -- transport never accepted" }
        if ($stats.writeFail -gt 0) { throw "stats.writeFail = $($stats.writeFail)" }
        $detail = "accepts=$($stats.accepts) sendFalse=$($stats.sendFalse) | $($cfg.geometry)"
    }
    catch {
        $ok = $false
        $detail = ($_.Exception.Message -replace "`r?`n", " ")
    }
    finally {
        if ($client) { $client.Dispose() }
        if ($p -and -not $p.HasExited) {
            try { $p.Kill() } catch { }
            try { $p.WaitForExit(5000) | Out-Null } catch { }
        }
    }
    $sw.Stop()

    if ($ok -and -not $KeepLogs) { Remove-Item $out, $err -EA SilentlyContinue }

    $results += [pscustomobject]@{
        Cell = $cell.Name; Result = $(if ($ok) { "PASS" } else { "FAIL" })
        Detail = $detail; Secs = [math]::Round($sw.Elapsed.TotalSeconds, 1)
    }
    Write-Host ("  {0,-22} {1,-4} {2,6:n1}s  {3}" -f $cell.Name, $(if ($ok) { "PASS" } else { "FAIL" }), $sw.Elapsed.TotalSeconds, $detail) -ForegroundColor $(if ($ok) { "Green" } else { "Red" })

    # Loopback ports go into TIME_WAIT; each cell gets a fresh one, but let teardown settle so a wedge
    # is attributable to the cell that caused it rather than to its successor.
    Start-Sleep -Milliseconds 400
}

Write-Host ""
$results | Export-Csv -NoTypeInformation (Join-Path $logDir "results.csv")
$results | Format-Table -AutoSize | Out-String | Write-Host
$failed = @($results | Where-Object { $_.Result -eq "FAIL" })
if ($failed.Count -gt 0) {
    Write-Host "$($failed.Count)/$($results.Count) FAILED - logs in $logDir" -ForegroundColor Red
    exit 1
}
Write-Host "all $($results.Count) cells PASS" -ForegroundColor Green
