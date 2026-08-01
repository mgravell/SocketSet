<#
.SYNOPSIS
    Prove the SChannel min-protocol floor is APPLIED, not merely configured: a TLS 1.2-only client must be
    REFUSED at the default floor and ACCEPTED with --tls-min12.

.DESCRIPTION
    The OpenSSL provider took a TLS 1.3 floor by default on 2026-07-31 (TlsOptions.MinProtocol); SChannel
    kept SP_PROT_DISABLE_BELOW_TLS1_2 and had no floor parameter at all, which left the two providers with
    DIFFERENT default security postures on the same library. This is the gate for closing that.

    House rule 2 is the whole design here. "TLS still works after the change" is NOT evidence the floor
    took — a floor that silently did nothing measures identically, because both configurations happily
    negotiate TLS 1.3 with a modern client. The only discriminating observation is a client that CANNOT do
    1.3: it must be REFUSED at the default floor and ACCEPTED at the opt-out. So the pre-registered
    falsifier is stated as a leg that must FAIL:

        default floor  + TLS1.2-only client  -> MUST be refused   (if it connects, the floor did nothing)
        default floor  + TLS1.3-only client  -> MUST connect, and report Tls13
        --tls-min12    + TLS1.2-only client  -> MUST connect, and report Tls12
        --tls-min12    + TLS1.3-only client  -> MUST connect, and report Tls13

    The last two matter as a control: without them, a floor that refused EVERYTHING would pass the first
    two cells and look like a working 1.3 floor.

.NOTES
    The probe is compiled C#, not a PowerShell scriptblock. A scriptblock certificate-validation callback
    fails at handshake time with "There is no Runspace available to run scripts in this thread" whenever
    the handshake runs off the calling thread — see the same trap recorded in Verify-AspNet.ps1.
#>
[CmdletBinding()]
param(
    [int]$FirstPort = 5450,
    [ValidateSet("iocp", "rio", "managed")][string[]]$Backends = @("iocp", "rio", "managed"),
    [switch]$KeepLogs
)

$ErrorActionPreference = "Stop"

$repo = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $repo "SmokeTest\SmokeTest.csproj"
$exe = Join-Path $repo "SmokeTest\bin\Release\net10.0\SmokeTest.exe"

Write-Host "building SmokeTest (Release) ..." -ForegroundColor Cyan
& dotnet build $proj -c Release -v q --nologo -f net10.0 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "build failed" }

Add-Type -TypeDefinition @"
using System;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;

public static class TlsFloorProbe
{
    // Returns the negotiated protocol name, or "REFUSED: <reason>". Never throws: a refusal is the
    // EXPECTED outcome of half the cells here, so it has to be a value the caller can compare.
    public static string Probe(string host, int port, SslProtocols protocols)
    {
        try
        {
            using (var tcp = new TcpClient())
            {
                tcp.Connect(host, port);
                using (var ssl = new SslStream(tcp.GetStream(), false, (s, c, ch, e) => true))
                {
                    ssl.AuthenticateAsClient(new SslClientAuthenticationOptions
                    {
                        TargetHost = "localhost",
                        EnabledSslProtocols = protocols,
                    });
                    return ssl.SslProtocol.ToString();
                }
            }
        }
        catch (Exception ex)
        {
            var inner = ex;
            while (inner.InnerException != null) inner = inner.InnerException;
            return "REFUSED: " + inner.Message;
        }
    }
}
"@ -ErrorAction SilentlyContinue

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$logDir = Join-Path $PSScriptRoot "results\tlsfloor-$stamp"
New-Item -ItemType Directory -Force $logDir | Out-Null

# Each floor, and what each client must see. "REFUSED" is an expectation, not a failure.
$floors = @(
    @{ Name = "default(1.3)"; Args = @(); Expect12 = "REFUSED"; Expect13 = "Tls13" }
    @{ Name = "--tls-min12";  Args = @("--tls-min12"); Expect12 = "Tls12"; Expect13 = "Tls13" }
)
$clients = @(
    @{ Name = "tls12-only"; Protocols = [System.Security.Authentication.SslProtocols]::Tls12; Key = "Expect12" }
    @{ Name = "tls13-only"; Protocols = [System.Security.Authentication.SslProtocols]::Tls13; Key = "Expect13" }
)

Write-Host ""
Write-Host "schannel floor: $($Backends.Count * $floors.Count * $clients.Count) cells -> $logDir" -ForegroundColor Cyan
Write-Host ""

$port = $FirstPort
$results = @()
foreach ($backend in $Backends) {
    foreach ($floor in $floors) {
        $port++
        $safe = "$backend-$($floor.Name)" -replace '[\\/:*?"<>|+-]', '_'
        $out = Join-Path $logDir "$safe.log"
        $err = Join-Path $logDir "$safe.err"
        $argList = @("--http", "--tls-schannel", "--$backend") + $floor.Args + @("--port", "$port")

        $p = Start-Process -FilePath $exe -ArgumentList $argList -PassThru -NoNewWindow `
            -RedirectStandardOutput $out -RedirectStandardError $err
        try {
            # Wait for the listener. A plain TCP connect is enough and does not consume a TLS handshake.
            $up = $false
            for ($i = 0; $i -lt 100; $i++) {
                if ($p.HasExited) { break }
                try { $t = [System.Net.Sockets.TcpClient]::new("127.0.0.1", $port); $t.Close(); $up = $true; break }
                catch { Start-Sleep -Milliseconds 100 }
            }
            if (-not $up) { throw "server never listened on $port (exited=$($p.HasExited))" }

            foreach ($client in $clients) {
                $got = [TlsFloorProbe]::Probe("127.0.0.1", $port, $client.Protocols)
                $want = $floor[$client.Key]
                # A refusal is matched by PREFIX: the SChannel alert text is not a stable contract, but
                # "did it refuse at all" is exactly the bit under test.
                $ok = if ($want -eq "REFUSED") { $got.StartsWith("REFUSED") } else { $got -eq $want }
                $results += [pscustomobject]@{
                    Cell = "$backend/$($floor.Name)/$($client.Name)"
                    Result = $(if ($ok) { "PASS" } else { "FAIL" })
                    Expected = $want
                    Got = $(if ($got.Length -gt 58) { $got.Substring(0, 58) + "..." } else { $got })
                }
                Write-Host ("  {0,-34} {1,-4} want={2,-8} got={3}" -f $results[-1].Cell, $results[-1].Result, $want, $results[-1].Got) `
                    -ForegroundColor $(if ($ok) { "Green" } else { "Red" })
            }
        }
        finally {
            if (-not $p.HasExited) { try { $p.Kill() } catch { }; try { $p.WaitForExit(5000) | Out-Null } catch { } }
            if (-not $KeepLogs) { Remove-Item $out, $err -EA SilentlyContinue }
        }
        Start-Sleep -Milliseconds 300
    }
}

Write-Host ""
$results | Export-Csv -NoTypeInformation (Join-Path $logDir "results.csv")
$results | Format-Table -AutoSize | Out-String | Write-Host
$failed = @($results | Where-Object { $_.Result -eq "FAIL" })
if ($failed.Count -gt 0) {
    Write-Host "$($failed.Count)/$($results.Count) FAILED" -ForegroundColor Red
    exit 1
}
Write-Host "all $($results.Count) cells PASS" -ForegroundColor Green
