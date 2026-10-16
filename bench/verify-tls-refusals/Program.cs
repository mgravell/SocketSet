using System.Buffers;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using SocketSets.Tls;
using SocketSets.Tls.Session;
using SocketSets.Tls.SslStreamBridge;

// THE TLS REFUSAL + INTEROP GATE (cross-platform; net11.0 — the tls-session provider only exists there):
//   dotnet run --project bench/verify-tls-refusals
//
// Refusal cells: the meaning lives in what must be REFUSED — "TLS still works" cannot distinguish a
// validation policy that took from one that did nothing, so the discriminating cells are the ones that
// must FAIL, with a control that must not (so the refusals aren't "everything fails"). Runs the battery
// for BOTH managed-engine providers (tls-session and sslstream), then two cross-engine interop cells
// sharing one certificate (TODO item 4's session↔sslstream half). This is an in-process filter pump, so
// it covers POLICY, not the wire: verify-tlsname's ClientHello-level SNI assertions and verify-tls-floor's
// below-floor-must-be-REFUSED cell remain separate (TODO item 3).
static (TlsHandshakeStatus client, TlsHandshakeStatus server, string? clientFault) Pump(TlsFilter client, TlsFilter server)
{
    var toServer = new ArrayBufferWriter<byte>();
    var toClient = new ArrayBufferWriter<byte>();
    var cs = client.DriveHandshake([], 0, toServer);
    var ss = TlsHandshakeStatus.NeedMoreData;
    for (int round = 0; round < 10 && (cs != TlsHandshakeStatus.Completed || ss != TlsHandshakeStatus.Completed); round++)
    {
        if (cs == TlsHandshakeStatus.Faulted || ss == TlsHandshakeStatus.Faulted) break;
        if (toServer.WrittenCount > 0 && ss != TlsHandshakeStatus.Completed)
        {
            var bytes = toServer.WrittenSpan.ToArray(); toServer.Clear();
            ss = server.DriveHandshake(bytes, 0, toClient);
        }
        if (toClient.WrittenCount > 0 && cs != TlsHandshakeStatus.Completed)
        {
            var bytes = toClient.WrittenSpan.ToArray(); toClient.Clear();
            cs = client.DriveHandshake(bytes, 0, toServer);
        }
        if (toServer.WrittenCount == 0 && toClient.WrittenCount == 0) break;
    }
    return (cs, ss, client.FaultReason);
}

int fails = 0;
void Cell(string name, bool expectAccept, TlsHandshakeStatus cs, string? fault)
{
    bool accepted = cs == TlsHandshakeStatus.Completed;
    bool ok = accepted == expectAccept;
    if (!ok) fails++;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}: client={cs} fault={fault ?? "(none)"}");
}

void Battery(string engine,
    Func<(TlsProvider provider, IDisposable owner)> loopback,
    Func<(TlsProvider provider, IDisposable owner)> clientSystemTrust,
    Func<(TlsProvider provider, IDisposable owner)> clientNoVerify)
{
    Console.WriteLine($"--- {engine} ---");
    {
        var (provider, owner) = loopback();
        using var _ = owner;
        { // control: must accept
            using var c = provider.CreateClientFilter(new TlsClientOptions { TargetHost = "localhost" });
            using var s = provider.CreateServerFilter(new TlsServerOptions());
            var (cs, _2, fault) = Pump(c, s);
            Cell($"{engine}/control/right-host", expectAccept: true, cs, fault);
        }
        { // wrong hostname: must refuse
            using var c = provider.CreateClientFilter(new TlsClientOptions { TargetHost = "wrong.example" });
            using var s = provider.CreateServerFilter(new TlsServerOptions());
            var (cs, _2, fault) = Pump(c, s);
            Cell($"{engine}/refuse/wrong-host", expectAccept: false, cs, fault);
        }
        { // AnyHost: no name check wanted -> accepts
            using var c = provider.CreateClientFilter(new TlsClientOptions { TargetHost = TlsClientAuthenticateContext.AnyHost });
            using var s = provider.CreateServerFilter(new TlsServerOptions());
            var (cs, _2, fault) = Pump(c, s);
            Cell($"{engine}/anyhost/no-name-check", expectAccept: true, cs, fault);
        }
    }
    { // untrusted: client with NO pinned trust (system store) must refuse the self-signed server
        var (serverProvider, serverOwner) = loopback();
        var (clientProvider, clientOwner) = clientSystemTrust();
        using var _s = serverOwner; using var _c = clientOwner;
        using var c = clientProvider.CreateClientFilter(new TlsClientOptions { TargetHost = "localhost" });
        using var s = serverProvider.CreateServerFilter(new TlsServerOptions());
        var (cs, _2, fault) = Pump(c, s);
        Cell($"{engine}/refuse/untrusted-chain", expectAccept: false, cs, fault);
    }
    { // verifyServer=false: the loud opt-out must actually opt out (inverse half)
        var (serverProvider, serverOwner) = loopback();
        var (clientProvider, clientOwner) = clientNoVerify();
        using var _s = serverOwner; using var _c = clientOwner;
        using var c = clientProvider.CreateClientFilter(new TlsClientOptions { TargetHost = "localhost" });
        using var s = serverProvider.CreateServerFilter(new TlsServerOptions());
        var (cs, _2, fault) = Pump(c, s);
        Cell($"{engine}/no-verify/accepts-untrusted", expectAccept: true, cs, fault);
    }
}

Battery("tls-session",
    () => { var p = TlsSessionProvider.CreateSelfSignedLoopback("localhost"); return (p, p); },
    () => { var p = new TlsSessionProvider(); return (p, p); },
    () => { var p = new TlsSessionProvider(verifyServer: false); return (p, p); });

Battery("sslstream",
    () => { var p = SslStreamTlsProvider.CreateSelfSignedLoopback("localhost"); return (p, p); },
    () => { var p = new SslStreamTlsProvider(); return (p, p); },
    () => { var p = new SslStreamTlsProvider(verifyServer: false); return (p, p); });

// Cross-engine interop: one certificate, a provider of each engine, client of one against server of the
// other — two independent wrappings of (on Linux) two independent paths into OpenSSL must agree.
Console.WriteLine("--- interop (shared cert) ---");
{
    using var rsa = RSA.Create(2048);
    var req = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    var san = new SubjectAlternativeNameBuilder();
    san.AddDnsName("localhost");
    san.AddIpAddress(IPAddress.Loopback);
    req.CertificateExtensions.Add(san.Build());
    req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
    using var ephemeral = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    byte[] pfx = ephemeral.Export(X509ContentType.Pfx, "pw");
    using var serverCert = X509CertificateLoader.LoadPkcs12(pfx, "pw", X509KeyStorageFlags.Exportable);
    using var trust = X509CertificateLoader.LoadCertificate(ephemeral.Export(X509ContentType.Cert));

    using var session = new TlsSessionProvider(serverCert, trust);
    using var sslstream = new SslStreamTlsProvider(serverCert, trust);

    {
        using var c = sslstream.CreateClientFilter(new TlsClientOptions { TargetHost = "localhost" });
        using var s = session.CreateServerFilter(new TlsServerOptions());
        var (cs, ss, fault) = Pump(c, s);
        Cell("interop/sslstream-client->session-server", expectAccept: true, cs, fault);
        if (ss != TlsHandshakeStatus.Completed) { fails++; Console.WriteLine($"FAIL interop server side: {ss}"); }
    }
    {
        using var c = session.CreateClientFilter(new TlsClientOptions { TargetHost = "localhost" });
        using var s = sslstream.CreateServerFilter(new TlsServerOptions());
        var (cs, ss, fault) = Pump(c, s);
        Cell("interop/session-client->sslstream-server", expectAccept: true, cs, fault);
        if (ss != TlsHandshakeStatus.Completed) { fails++; Console.WriteLine($"FAIL interop server side: {ss}"); }
    }
}

Console.WriteLine(fails == 0 ? "ALL PASS" : $"{fails} FAILED");
return fails;
