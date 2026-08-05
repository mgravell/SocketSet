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

// =====================================================================================================
// ANNOUNCE vs VERIFY (2026-08-05). TargetHost used to drive BOTH, so "*" meant no-SNI AND no-name-check
// as one indivisible choice, and "do not tell the server who I expect, but DO check what it presents"
// could not be said. TlsClientOptions.ServerNameIndication splits them.
//
// AND THE ASSERTION THAT MATTERS IS THE ONE THIS RIG COULD NOT PREVIOUSLY MAKE ON WINDOWS. The cells
// above read the announced name from the SERVER (Connection.RequestedServerName), which SChannel cannot
// report -- so every announce assertion was skipped here, which is exactly the half a
// suppress-the-SNI feature changes. Skipping the only assertion that can see the new behaviour would
// make these cells decorative.
//
// So this block does not ask the server. It reads the ClientHello OFF THE WIRE with a plain socket and
// parses server_name out of it, which works identically on both providers and both OSes. Parsing
// attacker-controlled bytes would want fuzzing; these are OUR OWN client's bytes, in a test, so the
// parser is bounds-checked and otherwise unceremonious.
Console.WriteLine();
Console.WriteLine("=== announce vs verify: what actually goes on the wire (ClientHello read directly) ===");

foreach (var (target, sni, expect, label) in new[]
{
    ("localhost",     (string)null, "localhost",     "derive: DNS name is announced"),
    ("127.0.0.1",     (string)null, null,            "derive: IP literal is NOT announced (RFC 6066)"),
    ("*",             (string)null, null,            "derive: AnyHost announces nothing"),
    ("localhost",     "*",          null,            "SPLIT: suppress announce, keep verifying"),
    ("localhost",     "front.example", "front.example", "SPLIT: announce a DIFFERENT name"),
})
{
    string seen; string detail = "";
    bool ok;
    try
    {
        using var sniffer = new SniSniffer(Port + 1);
        var clientOpts = new SocketSetOptions { Shards = 1, Factory = tlsBackend, Tls = (SocketSets.Tls.TlsProvider)provider };
        clientOpts.TlsClient.TargetHost = target;
        clientOpts.TlsClient.ServerNameIndication = sni;
        using var client = new Dialer(clientOpts);
        client.Connect(new IPEndPoint(IPAddress.Loopback, Port + 1));
        // The sniffer never completes a handshake, so the client will fault -- irrelevant. All that is
        // being asserted is what the client PUT IN ITS ClientHello.
        seen = sniffer.WaitForHello(TimeSpan.FromSeconds(5));
        ok = seen == expect;
        if (!ok) detail = $"announced {Show(seen)}, expected {Show(expect)}";
    }
    catch (Exception ex) { seen = null; ok = false; detail = ex.GetType().Name + ": " + ex.Message; }

    if (!ok) failures++;
    string cfg = $"host={Show(target)} sni={(sni is null ? "(derive)" : Show(sni))}";
    Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {cfg,-38} {label,-42} announced={Show(seen)} {detail}");
    Thread.Sleep(150);
}

// THE SECURITY CELL FOR THE SPLIT, and the reason the feature is not merely cosmetic: suppressing the
// announce must NOT weaken the check. Against the real server with a WRONG TargetHost and SNI off, the
// connection must still be REFUSED -- if splitting the fields quietly disabled verification, this is
// where it shows, and nothing above would notice.
{
    bool connected = false; string fault = "";
    try
    {
        var serverOpts = new SocketSetOptions { Shards = 1, Factory = tlsBackend, Tls = (SocketSets.Tls.TlsProvider)provider };
        using var server = new Echo(serverOpts);
        server.Listen(new IPEndPoint(IPAddress.Loopback, Port + 2));
        var clientOpts = new SocketSetOptions { Shards = 1, Factory = tlsBackend, Tls = (SocketSets.Tls.TlsProvider)provider };
        clientOpts.TlsClient.TargetHost = "wrong.example";
        clientOpts.TlsClient.ServerNameIndication = "*";   // announce nothing...
        using var client = new Dialer(clientOpts);
        client.Connect(new IPEndPoint(IPAddress.Loopback, Port + 2));
        connected = client.Opened.Wait(TimeSpan.FromSeconds(5));
        fault = client.Fault;
    }
    catch (Exception ex) { fault = ex.GetType().Name; }

    bool ok = !connected && fault.Length > 0;   // ...and STILL refuse the wrong name, with a reason
    if (!ok) failures++;
    Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {"host=wrong.example sni=\"*\"",-38} {"SPLIT: suppressing SNI must NOT skip the check",-42} "
                    + (connected ? "CONNECTED -- verification was disabled by the split" : $"REFUSED reason=\"{Trunc(fault)}\""));
}

Console.WriteLine(failures == 0
    ? (sniObservable
        ? "\n=== verify-tlsname: ALL PASS (including the refusal cells) ==="
        : "\n=== verify-tlsname: ALL PASS (server-side SNI unobservable here, but the ANNOUNCE cells read the wire directly) ===")
    : $"\n=== verify-tlsname: {failures} FAILURE(S) ===");
return failures == 0 ? 0 : 1;

static string Show(string s) => s is null ? "<none>" : "\"" + s + "\"";

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

/// <summary>
/// A plain TCP listener that accepts ONE connection, reads the first flight, and pulls the SNI out of
/// the ClientHello. It never replies, so the client's handshake fails — which is fine and is the point:
/// the only question is what the client ANNOUNCED, and that is fully determined by the bytes it sent
/// first. Provider- and OS-independent, which is why it exists: it is the only way this rig can assert
/// the announce half on SChannel, where the server side cannot report SNI at all.
/// </summary>
sealed class SniSniffer : IDisposable
{
    private readonly System.Net.Sockets.TcpListener _listener;
    private readonly Task<string> _hello;
    private volatile bool _sawHello;

    public SniSniffer(int port)
    {
        _listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        _hello = Task.Run(Accept);
    }

    private string Accept()
    {
        using var socket = _listener.AcceptSocket();
        var buf = new byte[16 * 1024];
        int have = 0;
        // Read until a whole record is in hand. One ClientHello is normally one segment on loopback, but
        // "normally" is not an invariant, so this loops on the record's own declared length.
        while (have < 5 || have < 5 + ((buf[3] << 8) | buf[4]))
        {
            int n = socket.Receive(buf, have, buf.Length - have, System.Net.Sockets.SocketFlags.None);
            if (n <= 0) break;
            have += n;
            if (have >= buf.Length) break;
        }
        _sawHello = have > 0;
        return ParseSni(buf.AsSpan(0, have));
    }

    /// <summary>The announced name, or null for "no server_name extension". Throws if no ClientHello
    /// arrived at all, so "nothing was announced" cannot be confused with "nothing was sent".</summary>
    public string WaitForHello(TimeSpan within)
    {
        if (!_hello.Wait(within)) throw new TimeoutException("no ClientHello arrived");
        if (!_sawHello) throw new InvalidOperationException("connection made but no bytes were sent");
        return _hello.Result;
    }

    // Bounds-checked walk to extension 0. Every step returns null rather than throwing on a short or
    // malformed buffer: a parse failure here must read as "could not observe", and the cell then fails
    // on the comparison rather than blowing up with a stack trace that hides which cell it was.
    private static string ParseSni(ReadOnlySpan<byte> b)
    {
        int p = 0;
        if (b.Length < 43 || b[0] != 0x16) return null;          // TLS handshake record
        p = 5;                                                   // skip record header
        if (b.Length < p + 4 || b[p] != 0x01) return null;       // ClientHello
        p += 4;                                                  // handshake type + 3-byte length
        p += 2 + 32;                                             // client_version + random
        if (b.Length < p + 1) return null;
        p += 1 + b[p];                                           // session_id
        if (b.Length < p + 2) return null;
        p += 2 + ((b[p] << 8) | b[p + 1]);                       // cipher_suites
        if (b.Length < p + 1) return null;
        p += 1 + b[p];                                           // compression_methods
        if (b.Length < p + 2) return null;
        int extEnd = p + 2 + ((b[p] << 8) | b[p + 1]);
        p += 2;
        while (p + 4 <= b.Length && p + 4 <= extEnd)
        {
            int type = (b[p] << 8) | b[p + 1];
            int len = (b[p + 2] << 8) | b[p + 3];
            p += 4;
            if (p + len > b.Length) return null;
            if (type == 0)                                       // server_name
            {
                int q = p + 2;                                   // skip server_name_list length
                if (q + 3 > b.Length) return null;
                if (b[q] != 0) return null;                      // name_type must be host_name
                int nameLen = (b[q + 1] << 8) | b[q + 2];
                q += 3;
                if (q + nameLen > b.Length) return null;
                return System.Text.Encoding.ASCII.GetString(b.Slice(q, nameLen));
            }
            p += len;
        }
        return null;                                             // no server_name extension present
    }

    public void Dispose()
    {
        try { _listener.Stop(); } catch { }
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
