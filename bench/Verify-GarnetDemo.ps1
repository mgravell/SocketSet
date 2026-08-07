<#
.SYNOPSIS
    Does GarnetDemo actually SERVE on this OS, on every backend it claims?

.DESCRIPTION
    Added 2026-08-07, when GarnetDemo was made hostable on Windows. It existed for months as a
    Linux-only host: it BUILT on Windows and died at construction with a PlatformNotSupportedException
    three frames deep, because the demo (not the library) hard-coded io_uring, OpenSSL and an absolute
    /home path.

    The discriminating assertion is a real RESP ROUND-TRIP, not a banner and not a process that stays
    up. A server that binds and never answers looks identical to a working one from the outside, and
    "it started" is exactly the evidence that would have passed before this change on Linux while
    proving nothing about Windows. So every cell dials the port, sends PING, and demands +PONG.

    The banner is checked TOO, and separately, because house rule 1 is trust-the-banner: a cell that
    asked for RIO and silently got IOCP would round-trip perfectly. Cells assert both.

    Also asserts the REFUSALS, which are the half that cannot be checked by running something: asking
    for a backend this OS cannot have must fail BY NAME with a usage error, not with a platform stack
    trace from inside the factory.
#>
[CmdletBinding()]
param(
    [int]$BasePort = 6410,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $repo "GarnetDemo/bin/$Configuration/net10.0/GarnetDemo.exe"
if (-not (Test-Path $exe)) { $exe = Join-Path $repo "GarnetDemo/bin/$Configuration/net10.0/GarnetDemo" }
if (-not (Test-Path $exe)) { throw "GarnetDemo not built: $exe" }

$isWin = $IsWindows -or ($env:OS -eq "Windows_NT")
$failures = 0

function Check($ok, $what, $detail) {
    if (-not $ok) { $script:failures++ }
    $tag = if ($ok) { "PASS" } else { "FAIL" }
    Write-Host ("  {0}  {1,-46} {2}" -f $tag, $what, $detail)
}

# One RESP round-trip. Inline PING on purpose: Garnet and Redis both accept the inline form, and it
# keeps this rig free of a RESP encoder.
#
# -Tls is not optional decoration: the first cut of this rig sent PLAINTEXT at the TLS port and reported
# a read timeout, which reads exactly like a broken server and was in fact a broken rig. A TLS cell has
# to speak TLS or it is testing nothing.
function Invoke-Ping([int]$Port, [switch]$Tls) {
    $client = [System.Net.Sockets.TcpClient]::new()
    try {
        $client.Connect("127.0.0.1", $Port)
        $client.ReceiveTimeout = 5000
        $stream = [System.IO.Stream]$client.GetStream()
        if ($Tls) {
            # The demo's certificate is self-signed by construction (generated, or the test material in
            # bench/.tools), so trust is not what this cell is testing -- the round-trip is.
            $ssl = [System.Net.Security.SslStream]::new($stream, $false, { param($s, $c, $ch, $e) $true })
            $ssl.AuthenticateAsClient("localhost")
            $stream = $ssl
        }
        $req = [Text.Encoding]::ASCII.GetBytes("PING`r`n")
        $stream.Write($req, 0, $req.Length); $stream.Flush()
        $buf = New-Object byte[] 64
        $n = $stream.Read($buf, 0, $buf.Length)
        return [Text.Encoding]::ASCII.GetString($buf, 0, $n)
    }
    finally { $client.Dispose() }
}

function Test-Leg($Name, [string[]]$ExtraArgs, $Port, $WantBanner, [switch]$Tls) {
    $outFile = Join-Path $env:TEMP "garnet-verify-$Port.out"
    $errFile = Join-Path $env:TEMP "garnet-verify-$Port.err"
    $argList = @("--port", $Port) + $ExtraArgs
    $proc = Start-Process -FilePath $exe -ArgumentList $argList -NoNewWindow -PassThru `
                          -RedirectStandardOutput $outFile -RedirectStandardError $errFile
    try {
        $deadline = (Get-Date).AddSeconds(25)
        $banner = $null
        while ((Get-Date) -lt $deadline) {
            if (Test-Path $outFile) {
                $lines = @(Get-Content $outFile -ErrorAction SilentlyContinue)
                # NOT -like here: '[' opens a character class in PowerShell wildcards, so "*[garnet-demo]*"
                # is a parse error rather than a literal match on the banner's own prefix.
                if ($lines -contains "ready") { $banner = ($lines | Where-Object { $_.Contains("garnet-demo") }) -join ""; break }
            }
            if ($proc.HasExited) { break }
            Start-Sleep -Milliseconds 250
        }

        if (-not $banner) {
            $err = (Get-Content $errFile -ErrorAction SilentlyContinue | Select-Object -First 3) -join " / "
            Check $false "$Name starts" "no banner; stderr: $err"
            return
        }

        Check ($banner -like "*$WantBanner*") "$Name banner says what it did" $banner.Trim()

        try   { $pong = Invoke-Ping -Port $Port -Tls:$Tls }
        catch { $pong = "EXCEPTION: $($_.Exception.Message)" }
        Check ($pong -eq "+PONG`r`n") "$Name serves a RESP round-trip" ("PING -> " + ($pong -replace "`r`n", "\r\n"))
    }
    finally {
        if (-not $proc.HasExited) { $proc.Kill(); [void]$proc.WaitForExit(5000) }
    }
}

# A refusal must be a USAGE error naming the flag, not a platform stack trace from inside the factory.
function Test-Refusal($Name, [string[]]$ExtraArgs, $WantText) {
    $errFile = Join-Path $env:TEMP "garnet-refuse-$($Name -replace '\W','').err"
    $outFile = "$errFile.out"
    $proc = Start-Process -FilePath $exe -ArgumentList $ExtraArgs -NoNewWindow -PassThru -Wait `
                          -RedirectStandardOutput $outFile -RedirectStandardError $errFile
    $text = (Get-Content $errFile -ErrorAction SilentlyContinue) -join " "
    $clean = ($proc.ExitCode -ne 0) -and ($text -like "*$WantText*") -and ($text -notlike "*Unhandled exception*")
    Check $clean "$Name is refused by name" "exit=$($proc.ExitCode) msg=$($text.Trim())"
}

Write-Host "GarnetDemo host check -- $exe"
Write-Host ("  os     : {0}" -f $(if ($isWin) { "Windows" } else { "Linux" }))
Write-Host ""

$port = $BasePort
if ($isWin) {
    Test-Leg "iocp"        @("--backend", "iocp")                $port      "transport=socketset/iocp"
    Test-Leg "rio"         @("--backend", "rio")                 ($port + 1) "transport=socketset/rio"
    Test-Leg "managed"     @("--backend", "managed")             ($port + 2) "transport=socketset/managed"
    Test-Leg "default"     @()                                   ($port + 3) "transport=socketset/iocp"
    Test-Leg "stock"       @("--stock")                          ($port + 4) "transport=garnet-saea tls=off"
    Test-Leg "iocp+tls"    @("--backend", "iocp", "--tls")       ($port + 5) "tls=schannel" -Tls
    # run-mux-ab.sh's exact server invocation, and it gates on this contiguous banner substring.
    Test-Leg "stock+tls"   @("--stock", "--tls")                 ($port + 6) "transport=garnet-saea tls=sslstream" -Tls
    Test-Refusal "epoll on Windows" @("--backend", "epoll")      "needs Linux"
    Test-Refusal "--ktls on Windows" @("--ktls")                 "SChannel"
}
else {
    Test-Leg "io-uring"    @("--backend", "io-uring")            $port      "transport=socketset/io-uring"
    Test-Leg "epoll"       @("--backend", "epoll")               ($port + 1) "transport=socketset/epoll"
    Test-Leg "managed"     @("--backend", "managed")             ($port + 2) "transport=socketset/managed"
    Test-Leg "default"     @()                                   ($port + 3) "transport=socketset/io-uring"
    Test-Leg "stock"       @("--stock")                          ($port + 4) "transport=garnet-saea tls=off"
    Test-Leg "io-uring+tls" @("--backend", "io-uring", "--tls")  ($port + 5) "tls=openssl" -Tls
    # run-mux-ab.sh's exact server invocation, and it gates on this contiguous banner substring.
    Test-Leg "stock+tls"   @("--stock", "--tls")                 ($port + 6) "transport=garnet-saea tls=sslstream" -Tls
    Test-Refusal "iocp on Linux" @("--backend", "iocp")          "needs Windows"
}

Write-Host ""
if ($failures -eq 0) { Write-Host "=== Verify-GarnetDemo: all cells PASS ===" }
else { Write-Host "=== Verify-GarnetDemo: $failures FAILURE(S) ===" }
exit $(if ($failures -eq 0) { 0 } else { 1 })
