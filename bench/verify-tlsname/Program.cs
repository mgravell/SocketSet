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

const int Port = 19871;
int failures = 0;

// One self-signed cert for "localhost", carrying DNS:localhost AND IP:127.0.0.1 as SANs, presented by
// the server on every cell. Only the CLIENT's expectation varies, which is what keeps the cells
// comparable: the certificate is the constant, the name being demanded is the variable.
using var provider = (IDisposable)GateBackends.SelfSigned();   // SChannel on Windows, OpenSSL elsewhere

// expectSni: what the SERVER should report as the name it was told, for the cells that connect.
// "<null>" means the client correctly sent NO SNI -- which is the whole point for "*" and for an IP
// literal (RFC 6066 forbids an address there), and is the only way to tell "we suppressed it" from
// "we sent it anyway and nobody noticed".
var (tlsBackend, backendName) = GateBackends.Tls[0];
Console.WriteLine($"=== verify-tlsname: backend={backendName} ===");

// THE SNI HALF OF THIS RIG DOES NOT EXIST ON EVERY PROVIDER, and it has to say so. SChannel offers no
// server-side way to read the name a client asked for, so Connection.RequestedServerName is always null on
// Windows. Left unsaid, the "127.0.0.1" and "*" cells -- whose whole content is "the client sent NO SNI" --
// would report sni=<null> and PASS there without observing anything at all: a provider that can never
// report SNI is indistinguishable from a client that correctly suppressed it. The accept/refuse cells are
// unaffected and remain fully discriminating on both providers; it is only the announce half that goes
// unmade, and an unmade assertion must be printed as unmade rather than as a pass.
bool sniObservable = GateBackends.ServerSniObservable;
if (!sniObservable)
{
    Console.WriteLine("  NOTE  server-side SNI is NOT OBSERVABLE on this provider (SChannel has no");
    Console.WriteLine("        SSL_get_servername equivalent), so the announce assertions below are");
    Console.WriteLine("        SKIPPED, not passed. The name-verification cells are unaffected.");
}

foreach (var (host, mustConnect, expectSni, label) in new[]
{
    ("localhost",     true,  "localhost", "DNS name matches SAN"),
    ("127.0.0.1",     true,  "<null>",    "IP literal matches iPAddress SAN"),
    ("127.0.0.2",     false, null,        "WRONG IP (must be refused)"),
    ("*",             true,  "<null>",    "AnyHost: explicit opt-out"),
    ("wrong.example", false, null,        "WRONG NAME (must be refused)"),
    ("",              false, null,        "unset (must be refused)"),
})
{
    bool connected = false;
    string detail = "";
    Echo echo = null;
    Dialer dialer = null;
    try
    {
        var serverOpts = new SocketSetOptions { Shards = 1, Factory = tlsBackend, Tls = (SocketSets.Tls.TlsProvider)provider };
        using var server = new Echo(serverOpts);
        echo = server;
        server.Listen(new IPEndPoint(IPAddress.Loopback, Port));

        var clientOpts = new SocketSetOptions { Shards = 1, Factory = tlsBackend, Tls = (SocketSets.Tls.TlsProvider)provider };
        clientOpts.TlsClient.TargetHost = host;
        using var client = new Dialer(clientOpts);
        dialer = client;
        client.Connect(new IPEndPoint(IPAddress.Loopback, Port));
        connected = client.Opened.Wait(TimeSpan.FromSeconds(5));
        // WAIT FOR THE SERVER TOO. Under TLS 1.3 the CLIENT considers the handshake done when it sends
        // its Finished, and the server only when it RECEIVES it -- so the client's signal fires first and
        // reading the server's view straight after it is a race. It duly failed that way on the first run.
        if (connected) server.Accepted.Wait(TimeSpan.FromSeconds(5));
    }
    catch (Exception ex)
    {
        // A configuration-time refusal (empty host) surfaces here rather than as a failed handshake.
        detail = ex.GetType().Name;
    }

    bool ok = connected == mustConnect;
    string sniSeen = echo?.Sni ?? "<no server>";
    bool sniChecked = sniObservable && mustConnect && expectSni is not null;
    if (ok && sniChecked && sniSeen != expectSni)
    {
        ok = false;
        detail = $"server was told SNI {sniSeen}, expected {expectSni}";
    }
    if (!ok) failures++;
    string verdict = connected ? "CONNECTED" : "REFUSED";
    // A refused cell must ALSO have produced a reason, or the refusal is undiagnosable in production.
    string fault = dialer?.Fault ?? "";
    if (ok && !mustConnect && host.Length > 0 && fault.Length == 0)
    {
        ok = false; failures++;
        detail = "refused with NO reason reported (OnTlsFault never fired)";
    }
    // Print what was ASSERTED, not merely what was seen: "sni=<null>" and "sni=(not observable)" are very
    // different claims and must not render alike.
    string sniNote = mustConnect
        ? (sniChecked ? $" sni={sniSeen}" : (mustConnect && expectSni is not null ? " sni=(not observable)" : ""))
        : $" reason=\"{Trunc(fault)}\"";
    Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {("\"" + host + "\""),-16} {label,-34} {verdict}{sniNote} {detail}");
    Thread.Sleep(200);
}

Console.WriteLine(failures == 0
    ? (sniObservable
        ? "\n=== verify-tlsname: ALL PASS (including the refusal cells) ==="
        : "\n=== verify-tlsname: ALL PASS (refusal cells included; SNI-announce assertions NOT MADE on this provider) ===")
    : $"\n=== verify-tlsname: {failures} FAILURE(S) ===");
return failures == 0 ? 0 : 1;

static string Trunc(string s) => s.Length <= 58 ? s : s[..55] + "...";

sealed class Echo(SocketSetOptions o) : SocketSet(o)
{
    // What the CLIENT asked for via SNI, as seen from the server, once the handshake is done. Null is a
    // real answer (no SNI sent), so it is distinguished from "no connection happened" by Saw.
    public volatile string Sni = "<none seen>";
    public readonly ManualResetEventSlim Accepted = new(false);

    protected override void OnAccept(ref AcceptContext ctx)
    {
        Sni = ctx.Connection.RequestedServerName ?? "<null>";
        Accepted.Set();
    }

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

    // A refusal must come with a REASON. Before 2026-08-04 it did not: the reason went to
    // Debug.WriteLine and so did not exist in a Release build, leaving "cannot connect" with no
    // diagnostic at all. Asserting the reason is non-empty is what stops that regressing.
    public volatile string Fault = "";

    protected override void OnConnect(ref ConnectContext ctx) => Opened.Set();

    protected override void OnTlsFault(Connection connection, string reason) => Fault = reason;
}
