// verify-tlsname — does hostname verification actually RUN, and does the explicit opt-out actually opt out?
//
// Until 2026-08-04 a null TargetHost silently skipped the name check on both providers while chain
// verification carried on, so a client accepted any certificate a trusted CA had ever issued, for any
// name. Nothing could see it: every existing gate connects to a server whose certificate is correct, and
// a name check that runs and a name check that is skipped are indistinguishable when the name matches.
//
// THE DISCRIMINATING CELL IS THEREFORE THE ONE THAT MUST BE REFUSED. Same rule as verify-tls-floor:
// "TLS still works" cannot tell an enforced check from an absent one. Cells:
//
//   localhost        ACCEPT   the DNS-SAN path
//   127.0.0.1        ACCEPT   the IP path. Every rig in bench/ dials by IP, so this is the cell that
//                             stops the mandatory-host change breaking all of them.
//   127.0.0.2        REFUSE   <-- and this is what makes the cell above mean something: it proves the
//                             name check RUNS for an address rather than being skipped for anything
//                             IP-shaped. NOTE it does NOT discriminate WHICH api does the matching:
//                             measured 2026-08-04, plain SSL_set1_host also accepts .1 and refuses .2
//                             against an IP-only SAN, contradicting the claim that drove the
//                             set1_ip_asc branch. See OpenSslTlsProvider.ApplyPeerName.
//   "*"              ACCEPT   the explicit opt-out: no SNI, no name check, chain still verified
//   wrong.example    REFUSE   <-- the one that proves the rest mean anything
//   "" (unset)       REFUSE   fails closed at configuration time, before a socket exists
//
// Exit 0 = all PASS.
using System.Net;
using SocketSets;
using SocketSets.Tls.OpenSsl;

const int Port = 19871;
int failures = 0;

// One self-signed cert for "localhost", carrying DNS:localhost AND IP:127.0.0.1 as SANs, presented by
// the server on every cell. Only the CLIENT's expectation varies, which is what keeps the cells
// comparable: the certificate is the constant, the name being demanded is the variable.
using var provider = OpenSslTlsProvider.CreateSelfSignedLoopback("localhost");

foreach (var (host, mustConnect, label) in new[]
{
    ("localhost",     true,  "DNS name matches SAN"),
    ("127.0.0.1",     true,  "IP literal matches iPAddress SAN"),
    ("127.0.0.2",     false, "WRONG IP (must be refused)"),
    ("*",             true,  "AnyHost: explicit opt-out"),
    ("wrong.example", false, "WRONG NAME (must be refused)"),
    ("",              false, "unset (must be refused)"),
})
{
    bool connected = false;
    string detail = "";
    try
    {
        var serverOpts = new SocketSetOptions { Shards = 1, Factory = SocketSetFactory.IoUring, Tls = provider };
        using var server = new Echo(serverOpts);
        server.Listen(new IPEndPoint(IPAddress.Loopback, Port));

        var clientOpts = new SocketSetOptions { Shards = 1, Factory = SocketSetFactory.IoUring, Tls = provider };
        clientOpts.TlsClient.TargetHost = host;
        using var client = new Dialer(clientOpts);
        client.Connect(new IPEndPoint(IPAddress.Loopback, Port));
        connected = client.Opened.Wait(TimeSpan.FromSeconds(5));
    }
    catch (Exception ex)
    {
        // A configuration-time refusal (empty host) surfaces here rather than as a failed handshake.
        detail = ex.GetType().Name;
    }

    bool ok = connected == mustConnect;
    if (!ok) failures++;
    string verdict = connected ? "CONNECTED" : "REFUSED";
    Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {("\"" + host + "\""),-16} {label,-34} {verdict} {detail}");
    Thread.Sleep(200);
}

Console.WriteLine(failures == 0
    ? "\n=== verify-tlsname: ALL PASS (including the refusal cells) ==="
    : $"\n=== verify-tlsname: {failures} FAILURE(S) ===");
return failures == 0 ? 0 : 1;

sealed class Echo(SocketSetOptions o) : SocketSet(o)
{
    protected override void OnReceive(ref ReceiveContext ctx)
    {
        if (!ctx.IsEof) ctx.ResponseBytes = ctx.PayloadBytes;
    }
}

sealed class Dialer(SocketSetOptions o) : SocketSet(o)
{
    // OnConnect fires only after the handshake COMPLETES, so it is exactly the signal wanted: a
    // certificate the client rejects never gets here, and the wait times out.
    public readonly ManualResetEventSlim Opened = new(false);

    protected override void OnConnect(ref ConnectContext ctx) => Opened.Set();
}
