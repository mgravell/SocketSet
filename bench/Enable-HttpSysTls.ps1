<#
.SYNOPSIS
    ONE-TIME, ELEVATED. Bind a certificate to a loopback port so http.sys can serve HTTPS, unblocking an
    http.sys TLS leg in the benchmark rigs. Everything else about the http.sys leg needs no elevation.

.DESCRIPTION
    The plaintext http.sys leg (`AspNetDemo --httpsys`) needs no setup at all: explicit-host prefixes
    (http://localhost:<port>/, http://127.0.0.1:<port>/) bind fine as a normal user; only the wildcard
    forms (+ and *) are refused. TLS is the one thing that genuinely does need administrator, and it is
    worth being precise about WHY, because it is not a URL-ACL problem:

      http.sys terminates TLS itself, in the kernel, using a certificate bound to an IP:PORT in the
      machine's SSL configuration. There is no API for an unprivileged process to say "use this
      certificate" -- unlike Kestrel and unlike our SChannel transport, both of which are handed a cert
      object in-process. So `netsh http add sslcert` (and writing to LocalMachine\My) is the elevation,
      and it is a per-machine configuration change rather than something the demo can do at startup.

    THE CERTIFICATE IS DELIBERATELY MATCHED to AspNetDemo's throwaway one (DemoCertificate): RSA-2048,
    SHA-256, CN=localhost, serverAuth EKU, DigitalSignature + KeyEncipherment. That file's own comment is
    the reason -- "a leg quietly running RSA-2048 against another running RSA-4096 (or ECDSA P-256) is
    measuring the certificate, not the transport". This leg cannot share the in-memory cert (http.sys
    reads from the machine store), so matching its SHAPE is the next best thing and the difference is
    then only "same parameters, different key", which does not move a handshake cost.

    STILL NOT IDENTICAL, and this belongs in any write-up that quotes the leg: http.sys does its crypto
    in the kernel with a machine-store key, so a TLS number from it is not the same experiment as
    Kestrel's SslStream or our in-transport SChannel. It is a useful outer bound, not a like-for-like row.

.PARAMETER Port
    Loopback port to bind. Deliberately NOT the rigs' default 5080: an sslcert binding makes that ip:port
    HTTPS, which would collide with the plaintext http.sys leg that also wants 5080.

.EXAMPLE
    # From an ELEVATED PowerShell:
    .\Enable-HttpSysTls.ps1

.EXAMPLE
    # Undo it:
    .\Enable-HttpSysTls.ps1 -Remove
#>
[CmdletBinding()]
param(
    [int]$Port = 5443,
    [switch]$Remove
)

$ErrorActionPreference = "Stop"

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "This script must run ELEVATED (it writes LocalMachine\My and changes the machine SSL config). " +
          "Everything else about the http.sys leg does NOT need elevation -- only this."
}

# A stable, arbitrary GUID identifying these bindings as ours, so -Remove can be sure what it is deleting.
$appid = "{6f1d4a7c-1f2b-4c33-9a55-5e2c0a7b91de}"
$friendly = "SocketSet bench http.sys (throwaway)"

if ($Remove) {
    & netsh http delete sslcert ipport=127.0.0.1:$Port | Out-Host
    Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.FriendlyName -eq $friendly } | ForEach-Object {
        Write-Host "removing certificate $($_.Thumbprint)"
        Remove-Item "Cert:\LocalMachine\My\$($_.Thumbprint)" -Force
    }
    Write-Host "removed." -ForegroundColor Green
    return
}

# Reuse an existing one rather than accumulating a new throwaway cert per run.
$cert = Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.FriendlyName -eq $friendly } | Select-Object -First 1
if (-not $cert) {
    Write-Host "creating certificate (RSA-2048/SHA256, CN=localhost) ..." -ForegroundColor Cyan
    $cert = New-SelfSignedCertificate `
        -Subject "CN=localhost" `
        -DnsName "localhost", "127.0.0.1" `
        -CertStoreLocation "Cert:\LocalMachine\My" `
        -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 `
        -KeyUsage DigitalSignature, KeyEncipherment `
        -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.1") `
        -NotAfter (Get-Date).AddYears(1) `
        -FriendlyName $friendly
}
Write-Host "certificate: $($cert.Thumbprint)  $($cert.Subject)"

# Replace any prior binding on this ip:port so re-running is idempotent rather than an error.
& netsh http delete sslcert ipport=127.0.0.1:$Port 2>&1 | Out-Null
& netsh http add sslcert ipport=127.0.0.1:$Port certhash=$($cert.Thumbprint) appid=$appid | Out-Host
if ($LASTEXITCODE -ne 0) { throw "netsh http add sslcert failed with $LASTEXITCODE" }

Write-Host ""
Write-Host "bound https on 127.0.0.1:$Port" -ForegroundColor Green
Write-Host "now runnable unelevated:  AspNetDemo --httpsys --tls --port $Port" -ForegroundColor Green
Write-Host "the cert is self-signed, so clients must skip verification (curl -k / bombardier -k)."
