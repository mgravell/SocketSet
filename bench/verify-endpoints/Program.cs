// verify-endpoints (added 2026-08-08): that Connection.RemoteAddress/LocalAddress report the REAL peer,
// that a recycled connection cannot report the PREVIOUS tenant's, and that the TrackEndpoints opt-out is
// neither inert nor leaking into the default.
//
// WHY THIS RIG EXISTS AT ALL. Nothing in the tree read a peer endpoint before this, so nothing could see
// that SocketSetNetworkSender was answering Garnet's "is this peer local?" with a hard-coded true --
// which is REVIEW.md F9, and is how a DEBUG/MODULE restriction the operator opted into became a no-op.
// A gate for endpoints is therefore a security gate, not a cosmetic one.
//
// THE CELLS THAT DISCRIMINATE, and the ones that do not:
//
//   loopback/reports        CONTROL. A 127.0.0.1 peer must report 127.0.0.1 and IsLoopback. Passes
//                           against almost any implementation, including a hard-coded one -- which is
//                           exactly why it cannot be the only cell.
//   lan/not-loopback        DISCRIMINATING. Connect over this box's own LAN address: the peer address
//                           must be that address and IsLoopback must be FALSE. A hard-coded "true", or a
//                           parser with the octets reversed, dies here and nowhere else. Reports
//                           INCONCLUSIVE (not PASS) when the host has no usable LAN address, because a
//                           silently-skipped cell is how this class of bug survives.
//   port/distinct           Two simultaneous clients must report DIFFERENT peer ports. This is what
//                           CLIENT KILL ADDR and CLIENT LIST filtering actually depend on, and a rig that
//                           only checked the address would pass while every client stayed
//                           indistinguishable.
//   recycle/new-tenant      DISCRIMINATING. Slots are pooled. Churn connections from known-distinct
//                           source ports and require each to report ITS OWN -- never a previous tenant's.
//                           An implementation that populates on success but forgets to clear on claim
//                           passes every other cell here.
//   tracking-off/unset      THE INVERSE HALF, per the verify-tailwipe pattern. With TrackEndpoints=false
//                           the address must be UNSET and IsLoopback FALSE. An inert flag leaves the
//                           on-cells green; a flag that leaked into the default leaves this one green;
//                           only the pair can tell those apart.
//   banner/says-what-it-did The set must REPORT endpoints=on|off|unsupported, so a harness can gate on
//                           what happened rather than on what was asked for (house rule 1).
//   connect/reports         DISCRIMINATING, and added 2026-08-09 with the fix for the gap it missed. Every
//                           cell above is INBOUND; outbound connections reported nothing on any native
//                           backend and this rig passed 21/21 without noticing. Dial a plain TcpListener
//                           and require BOTH halves. The remote half alone is weak -- we asked for that
//                           address, so an implementation that simply remembered the EndPoint it was
//                           handed would pass it. THE LOCAL HALF IS WHAT DISCRIMINATES: LocalAddress.Port
//                           must equal the ephemeral port the TARGET saw the connection arrive from, which
//                           only the kernel knows and no amount of remembering can produce. It is also the
//                           only cell that can catch the epoll bug specifically -- populating at
//                           slot-claim, BEFORE a non-blocking connect completes, so getpeername returned
//                           ENOTCONN and both addresses silently stayed unset.
//   connect-off/unset       The inverse half for outbound, matching tracking-off/unset.
//
// On a backend that declines tracking (io_uring: multishot accept fills no address buffer), the rig
// asserts the DOCUMENTED degradation -- unsupported in the banner, addresses unset -- rather than
// skipping, so a backend that quietly started or stopped supporting it fails. That covers the outbound
// cells too: io_uring COULD populate on connect (it holds a real fd there, and the per-accept cost
// argument does not apply), and deliberately does not, so that endpoints=unsupported stays literally true
// rather than meaning "unsupported in one direction". connect/declines-unset is what holds that decision
// in place.
//
// NO OUTBOUND lan/ CELL, deliberately: dialling this box's LAN address proves much less than receiving
// from it, because the address under test is one we supplied. The inbound lan/not-loopback cell above is
// where a reversed-octet parse or a hard-coded locality answer dies, and both directions share that parser.

using System.Net;
using System.Net.Sockets;
using System.Text;
using SocketSets;

int failures = 0, inconclusive = 0;
// Fixed, as the other verify rigs do. THREE per backend now: the set's listener, the tracking-off set's
// listener, and a plain TcpListener for the outbound cells to dial.
int nextPort = 19940;

void Cell(string name, bool ok, string detail)
{
    Console.WriteLine($"  {(ok ? "PASS" : "FAIL"),-4}  {name,-26} {detail}");
    if (!ok) failures++;
}

void Inconclusive(string name, string detail)
{
    Console.WriteLine($"  {"????",-4}  {name,-26} INCONCLUSIVE: {detail}");
    inconclusive++;
}

// This box's own LAN address, if it has one. Connecting to it still carries it as the destination, so a
// loopback-only implementation cannot match it -- the same trick Verify-BindReachability uses to ask a
// network question without a second machine.
static IPAddress FindLanAddress()
{
    try
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Connect(new IPEndPoint(IPAddress.Parse("192.0.2.1"), 9)); // TEST-NET-1: routed, never answers
        var local = (IPEndPoint)probe.LocalEndPoint;
        return IPAddress.IsLoopback(local.Address) ? null : local.Address;
    }
    catch { return null; }
}

foreach (var (factory, backendName) in GateBackends.All)
{
    Console.WriteLine();
    Console.WriteLine($"=== {backendName} ===");

    // ---------- tracking ON ----------
    Harness set;
    try
    {
        set = new Harness(new SocketSetOptions { Factory = factory, Shards = 1, TrackEndpoints = true });
    }
    catch (PlatformNotSupportedException ex)
    {
        Inconclusive($"{backendName}/unavailable", ex.Message);
        continue;
    }

    int port = nextPort, offPort = nextPort + 1, dialPort = nextPort + 2;
    nextPort += 3;

    // The OUTBOUND target: a plain listener, deliberately not the set's own, so an outbound connect never
    // lands in the accept-side Seen list that the cells below index with Last(). Kept alive across both
    // halves (tracking on, then off).
    var dialTarget = new TcpListener(IPAddress.Loopback, dialPort);
    dialTarget.Start();

    using (set)
    {
        // IPAddress.Any, not Loopback: the discriminating lan/ cell has to reach this listener over a
        // non-loopback address, and a loopback-bound listener would refuse it for the wrong reason.
        set.Listen(new IPEndPoint(IPAddress.Any, port));

        string banner = set.ToString();
        bool supported = set.SupportsEndpointTracking;
        string wantBanner = supported ? "endpoints=on" : "endpoints=unsupported";
        Cell("banner/says-what-it-did", banner.Contains(wantBanner), $"'{wantBanner}' in: {banner}");

        // ---- control: a loopback peer ----
        using (var c = new TcpClient())
        {
            c.Connect(IPAddress.Loopback, port);
            c.GetStream().Write("x"u8);
            c.GetStream().ReadByte();
            var localPort = ((IPEndPoint)c.Client.LocalEndPoint).Port;
            Thread.Sleep(50);
            var seen = Snapshot(set);
            var rec = seen.LastOrDefault();

            if (!supported)
            {
                Cell("declines/unset", !rec.Remote.IsSet && !rec.Remote.IsLoopback,
                     $"documented degradation: remote={Show(rec.Remote)} IsLoopback={rec.Remote.IsLoopback}");
            }
            else
            {
                Cell("loopback/reports",
                     rec.Remote.IsSet && rec.Remote.IsLoopback && rec.Remote.Port == localPort
                         && rec.Local.IsSet && rec.Local.Port == port,
                     $"remote={Show(rec.Remote)} (client port {localPort}), local={Show(rec.Local)}");
            }
        }

        // ---- OUTBOUND: the direction that reported nothing until 2026-08-09 ----
        {
            var dial = DialOut(set, dialTarget);
            if (dial.Error.Length != 0)
            {
                Cell("connect/reports", false, $"outbound dial failed: {dial.Error}");
            }
            else if (!supported)
            {
                Cell("connect/declines-unset",
                     !dial.Remote.IsSet && !dial.Local.IsSet && !dial.Remote.IsLoopback,
                     $"documented degradation, both directions: remote={Show(dial.Remote)} local={Show(dial.Local)}");
            }
            else
            {
                // The local half is the load-bearing one: the target saw this connection arrive from an
                // ephemeral port nobody supplied, so matching it cannot be done by remembering the dial.
                Cell("connect/reports",
                     dial.Remote.IsSet && dial.Remote.IsLoopback && dial.Remote.Port == dialPort
                         && dial.Local.IsSet && dial.ServerSawPort != 0 && dial.Local.Port == dial.ServerSawPort,
                     $"remote={Show(dial.Remote)} (dialled 127.0.0.1:{dialPort}), local={Show(dial.Local)} "
                         + $"(target saw client port {dial.ServerSawPort})");
            }
        }

        if (supported)
        {
            // ---- discriminating: two clients must be distinguishable ----
            using (var a = new TcpClient())
            using (var b = new TcpClient())
            {
                a.Connect(IPAddress.Loopback, port);
                b.Connect(IPAddress.Loopback, port);
                a.GetStream().Write("x"u8); a.GetStream().ReadByte();
                b.GetStream().Write("x"u8); b.GetStream().ReadByte();
                Thread.Sleep(50);
                var seen = Snapshot(set);
                var two = seen.TakeLast(2).ToArray();
                Cell("port/distinct",
                     two.Length == 2 && two[0].Remote.Port != two[1].Remote.Port && two[0].Remote.Port != 0,
                     two.Length == 2 ? $"{Show(two[0].Remote)} vs {Show(two[1].Remote)}" : "did not observe two accepts");
            }

            // ---- discriminating: a pooled slot must not report the previous tenant ----
            var ports = new List<int>();
            var reported = new List<int>();
            for (int i = 0; i < 8; i++)
            {
                using var c = new TcpClient();
                c.Connect(IPAddress.Loopback, port);
                c.GetStream().Write("x"u8);
                c.GetStream().ReadByte();
                ports.Add(((IPEndPoint)c.Client.LocalEndPoint).Port);
                Thread.Sleep(30);
                reported.Add(Snapshot(set).Last().Remote.Port);
            }
            bool matched = ports.Count == reported.Count && !ports.Where((p, i) => p != reported[i]).Any();
            Cell("recycle/new-tenant", matched,
                 matched ? $"{ports.Count} churned connections each reported their own port"
                         : $"expected {string.Join(",", ports)}, got {string.Join(",", reported)}");

            // ---- discriminating: a NON-loopback peer must not read as local ----
            var lan = FindLanAddress();
            if (lan is null)
            {
                Inconclusive("lan/not-loopback", "no non-loopback IPv4 address on this host");
            }
            else
            {
                try
                {
                    using var c = new TcpClient();
                    c.Connect(lan, port);
                    c.GetStream().Write("x"u8);
                    c.GetStream().ReadByte();
                    Thread.Sleep(50);
                    var rec = Snapshot(set).Last();
                    Cell("lan/not-loopback",
                         rec.Remote.IsSet && !rec.Remote.IsLoopback && Show(rec.Remote).StartsWith(lan.ToString() + ":"),
                         $"peer {Show(rec.Remote)} via {lan}, IsLoopback={rec.Remote.IsLoopback} (must be False)");
                }
                catch (SocketException ex)
                {
                    Inconclusive("lan/not-loopback", $"could not reach {lan}: {ex.SocketErrorCode} (host firewall?)");
                }
            }
        }
    }

    // ---------- tracking OFF: the inverse half ----------
    using (var off = new Harness(new SocketSetOptions { Factory = factory, Shards = 1, TrackEndpoints = false }))
    {
        off.Listen(new IPEndPoint(IPAddress.Any, offPort));
        Cell("banner/off", off.ToString().Contains("endpoints=off"), off.ToString());

        using var c = new TcpClient();
        c.Connect(IPAddress.Loopback, offPort);
        c.GetStream().Write("x"u8);
        c.GetStream().ReadByte();
        Thread.Sleep(50);
        var rec = Snapshot(off).LastOrDefault();
        Cell("tracking-off/unset", !rec.Remote.IsSet && !rec.Remote.IsLoopback,
             $"remote={Show(rec.Remote)} IsSet={rec.Remote.IsSet} IsLoopback={rec.Remote.IsLoopback} (both must be False)");

        // The same inverse for outbound: an opt-out that only covers accepts is still a leak, and the
        // outbound populate is a separate call site on every backend, so it needs its own assertion.
        var dial = DialOut(off, dialTarget);
        Cell("connect-off/unset",
             dial.Error.Length == 0 && !dial.Remote.IsSet && !dial.Local.IsSet && !dial.Remote.IsLoopback,
             dial.Error.Length != 0 ? $"outbound dial failed: {dial.Error}"
                 : $"remote={Show(dial.Remote)} local={Show(dial.Local)} (both must be unset)");
    }

    dialTarget.Stop();
}

Console.WriteLine();
if (failures == 0 && inconclusive == 0) Console.WriteLine("=== verify-endpoints: all cells PASS ===");
else if (failures == 0) Console.WriteLine($"=== verify-endpoints: PASS with {inconclusive} INCONCLUSIVE ===");
else Console.WriteLine($"=== verify-endpoints: {failures} FAILURE(S), {inconclusive} inconclusive ===");
return failures == 0 ? 0 : 1;

static List<(PeerAddress Remote, PeerAddress Local)> Snapshot(Harness h)
{
    lock (h.Seen) return new List<(PeerAddress, PeerAddress)>(h.Seen);
}

static string Show(PeerAddress a) => a.IsSet ? a.ToString() : "<unset>";

// Make ONE outbound connection from the set to a plain listener, and report three things: what the set
// says the peer is, what it says WE are, and what the target independently saw the connection arrive
// from. The third is the cross-check -- it is the only number here the set could not have produced from
// what it was handed.
static (PeerAddress Remote, PeerAddress Local, int ServerSawPort, string Error) DialOut(Harness h, TcpListener target)
{
    Socket accepted = null;
    try
    {
        h.ResetConnect();
        h.Connect(new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)target.LocalEndpoint).Port));
        accepted = target.AcceptSocket(); // blocks until the outbound connect actually lands
        if (!h.WaitForConnect(TimeSpan.FromSeconds(5)))
            return (default, default, 0, "OnConnect never fired within 5s");
        var sawPort = ((IPEndPoint)accepted.RemoteEndPoint).Port;
        return (h.ConnectRemote, h.ConnectLocal, sawPort, "");
    }
    catch (Exception ex) { return (default, default, 0, $"{ex.GetType().Name}: {ex.Message}"); }
    finally { accepted?.Dispose(); }
}

sealed class Harness : SocketSet
{
    public readonly List<(PeerAddress Remote, PeerAddress Local)> Seen = new();
    public Harness(SocketSetOptions o) : base(o) { }

    // Outbound, recorded separately from Seen so the accept-side cells can keep indexing it with Last().
    public PeerAddress ConnectRemote, ConnectLocal;
    private readonly ManualResetEventSlim _connected = new(false);

    public void ResetConnect()
    {
        ConnectRemote = ConnectLocal = default;
        _connected.Reset();
    }

    public bool WaitForConnect(TimeSpan timeout) => _connected.Wait(timeout);

    protected override void OnAccept(ref AcceptContext ctx)
    {
        lock (Seen) Seen.Add((ctx.Connection.RemoteAddress, ctx.Connection.LocalAddress));
    }

    protected override void OnConnect(ref ConnectContext ctx)
    {
        // Read from the callback, not afterwards: "valid from OnConnect onwards" is the documented
        // contract, and a rig that sampled later would pass an implementation that populated too late for
        // anyone to use.
        ConnectRemote = ctx.Connection.RemoteAddress;
        ConnectLocal = ctx.Connection.LocalAddress;
        _connected.Set();
    }

    protected override void OnReceive(ref ReceiveContext ctx)
        => ctx.Connection.Send("k"u8); // echo, so the client knows the accept has been observed
}
