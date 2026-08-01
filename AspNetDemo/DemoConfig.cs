using System.Runtime.Versioning;
using SocketSets.Tls;

namespace SocketSets.AspNet;

/// <summary>
/// The A/B matrix for this demo, parsed from the command line. The point of the demo is comparing
/// configurations, so every axis that changes what is being measured is a flag rather than a rebuild:
///
///   transport — Kestrel's own sockets ("vanilla", the control) vs the SocketSet transport, and within
///               that which backend (auto-detected, or forced to managed / IOCP / RIO / io_uring).
///   TLS       — off, terminated in the transport (SChannel on Windows, OpenSSL on Linux), or terminated
///               with kernel offload (kTLS: Linux, io_uring or epoll). Vanilla Kestrel does its own HTTPS
///               via SslStream, which is exactly the comparison worth having.
///
/// Every configuration is pinned to HTTP/1.1 so the legs stay comparable; see Program.cs.
/// </summary>
internal sealed class DemoConfig
{
    /// <summary>Use Kestrel's built-in socket transport (and, with <see cref="Tls"/>, its own SslStream
    /// HTTPS) instead of anything from this repo — the control leg.</summary>
    public bool VanillaKestrel { get; private set; }

    /// <summary>Which SocketSet backend to ask for. Kept as our own enum because
    /// <see cref="SocketSetFactory"/> is an abstract class of static instances, not an enum — so it
    /// cannot be switched on, and "which one did the user pick" has to be tracked separately from
    /// "which instance did that resolve to".</summary>
    public enum Backend { Auto, Managed, Iocp, Rio, IoUring, Epoll }

    public Backend Which { get; private set; } = Backend.Auto;

    /// <summary>The resolved factory. <see cref="Backend.Auto"/> maps to
    /// <see cref="SocketSetFactory.Default"/>, which picks IOCP on Windows and io_uring on Linux.</summary>
    public SocketSetFactory Factory => Which switch
    {
        Backend.Managed => SocketSetFactory.Managed,
        Backend.Iocp => SocketSetFactory.WindowsIocp,
        Backend.Rio => SocketSetFactory.WindowsRio,
        Backend.IoUring => SocketSetFactory.IoUring,
        Backend.Epoll => SocketSetFactory.Epoll,
        _ => SocketSetFactory.Default,
    };

    public bool Tls { get; private set; }

    /// <summary>Kernel-TLS offload. Implies <see cref="Tls"/>; Linux + io_uring only.</summary>
    public bool Ktls { get; private set; }

    public int Shards { get; private set; }
    public bool Pin { get; private set; }
    public int Port { get; private set; } = 5080;
    public bool Help { get; private set; }

    /// <summary>Send/write page size; 0 leaves the library default. RIO cannot scatter-gather, so one
    /// send is one page and this is the only lever it has on large responses - measured 2026-07-27, a
    /// 64KB page is worth 4.68x at a 256KB payload. Exposed here so the bridged path can be swept the
    /// same way the bare responder can.</summary>
    public int PageSize { get; private set; }

    /// <summary>Per-socket receive buffer size; 0 follows <see cref="PageSize"/> (the library default).
    /// Separate because there is one per SOCKET, so it multiplies by SocketsPerShard where the send page
    /// does not: coupling them made a 64KB page cost 3.1GB rather than 283MB.</summary>
    public int RecvBufferSize { get; private set; }

    /// <summary>Write buffers per shard; 0 leaves the library default (1024). The write slab is this
    /// times <see cref="PageSize"/>, which is the OTHER memory term - at a 64KB page, 1024 buffers is
    /// 64MB per shard. Shrinking it is not free: running the pool dry currently closes the connection
    /// rather than queueing.</summary>
    public int WriteBuffers { get; private set; }

    /// <summary>
    /// BYO-buffer bridge: hand Kestrel's own pipes to the transport via ctx.UsePipe instead of copying
    /// inbound and running an outbound pump in SocketSetConnection.
    ///
    /// **ON by default since 2026-07-31**, because it stopped being a research path and became the better
    /// one at every size measured. With a same-session vanilla-Kestrel control it is at parity at 256KB
    /// and +14.2% at 1MB, where the classic bridge is -60.3% and -52.6%. `--classic` turns it off; the
    /// classic path is kept because it is the control every zero-copy claim is measured against, and
    /// because it is the only path on backends that cannot do zero-copy send at all (RIO, managed).
    /// </summary>
    public bool ByoPipe { get; private set; } = true;

    /// <summary>Whether <see cref="ByoPipe"/> was asked for explicitly, so `--kestrel` can turn the
    /// default off silently while still rejecting an explicit `--kestrel --byo`, which is a genuine
    /// contradiction rather than a default that does not apply.</summary>
    private bool _byoExplicit;

    /// <summary>Pipe block size for the bridge's pipes (0 = framework default, ~4KB). At 4KB a 256KB
    /// response is ~65 pipe segments; at 64KB it is ~5. Drives iovec count, WriteAll iterations and (on
    /// the zero-copy send path) the number of per-segment pins.</summary>
    public int PipeSegment { get; private set; }

    /// <summary>Back the bridge's pipes with a pinned-block pool and assert `pinned: true` to the transport,
    /// so a zero-copy send skips per-segment GCHandle pinning entirely. DEFAULT TRUE (2026-07-31): this
    /// matches vanilla Kestrel, which uses a PinnedBlockMemoryPool by default — an UNPINNED default made our
    /// zero-copy send pay ~64 GCHandle pins per 256KB response that Kestrel does not, which was the ENTIRE
    /// "bridge trails Kestrel at 256KB" gap (measured: pinned reaches parity/ahead). `--pipe-unpinned` opts
    /// out (it costs less pinned RSS but reintroduces the per-segment pin, so it is the unfair comparison).</summary>
    public bool PipePinned { get; private set; } = true;

    /// <summary>EXPERIMENT (branch cyclebuffer-halfpipe): the outbound "half-pipe". Replace the outbound
    /// <c>Pipe</c> with a <c>CycleBuffer</c>-backed <c>PipeWriter</c> that drains itself to
    /// <c>Connection.Send</c> on Kestrel's flush thread — no pump task, no ThreadPool hop, no async read
    /// loop. Copies on send (not zero-copy), so it targets the small/mid payloads and the per-connection
    /// pump/hop cost, not 256KB throughput. Mutually exclusive with BYO (turns it off) — it IS the outbound
    /// path, not a pipe handed to the transport. Inbound stays a stock Pipe.</summary>
    public bool HalfPipe { get; private set; }

    public string Scheme => Tls ? "https" : "http";

    private DemoConfig() { }

    public static DemoConfig Parse(string[] args)
    {
        // SS_SHARDS / SS_PIN stay as the defaults (the README's benchmarking knobs); flags override them.
        var cfg = new DemoConfig
        {
            Shards = int.TryParse(Environment.GetEnvironmentVariable("SS_SHARDS"), out var s) ? s : 2,
            Pin = Environment.GetEnvironmentVariable("SS_PIN") == "1",
        };

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                // --- transport ---
                case "--kestrel":
                case "--default-transport": cfg.VanillaKestrel = true; break;
                case "--managed": cfg.Which = Backend.Managed; break;
                case "--iocp": cfg.Which = Backend.Iocp; break;
                case "--rio": cfg.Which = Backend.Rio; break;
                case "--io-uring": cfg.Which = Backend.IoUring; break;
                case "--epoll": cfg.Which = Backend.Epoll; break;

                // --- TLS ---
                case "--tls": cfg.Tls = true; break;
                case "--ktls": cfg.Tls = cfg.Ktls = true; break;

                // --- shape ---
                case "--shards" when i + 1 < args.Length && int.TryParse(args[i + 1], out var n): cfg.Shards = n; i++; break;
                case "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], out var p): cfg.Port = p; i++; break;
                case "--pin": cfg.Pin = true; break;
                case "--no-pin": cfg.Pin = false; break;
                case "--page" when i + 1 < args.Length && int.TryParse(args[i + 1], out var pg): cfg.PageSize = pg; i++; break;
                case "--recv-buffer" when i + 1 < args.Length && int.TryParse(args[i + 1], out var rb): cfg.RecvBufferSize = rb; i++; break;
                case "--write-buffers" when i + 1 < args.Length && int.TryParse(args[i + 1], out var wb): cfg.WriteBuffers = wb; i++; break;
                case "--byo": cfg.ByoPipe = true; cfg._byoExplicit = true; break;
                // Opt OUT of the default bridge. Kept because the classic path is the control every
                // zero-copy claim is measured against, and it is the only path RIO and managed can take.
                case "--classic":
                case "--no-byo": cfg.ByoPipe = false; cfg._byoExplicit = true; break;
                case "--pipe-segment" when i + 1 < args.Length && int.TryParse(args[i + 1], out var ps):
                    cfg.PipeSegment = ps; i++; break;
                case "--pipe-pinned": cfg.PipePinned = true; break;
                case "--pipe-unpinned": cfg.PipePinned = false; break; // opt out of the (default) pinned pool
                // EXPERIMENT: outbound half-pipe (CycleBuffer PipeWriter -> direct Send). Turns BYO off:
                // it replaces the outbound leg rather than handing a pipe to the transport.
                case "--half-pipe": cfg.HalfPipe = true; cfg.ByoPipe = false; break;

                case "-h":
                case "--help": cfg.Help = true; break;

                default: throw new ArgumentException($"unrecognised argument '{args[i]}' (try --help)");
            }
        }

        cfg.Validate();
        return cfg;
    }

    /// <summary>Reject combinations that cannot work on this OS, loudly and early — the alternative is a
    /// confusing failure deep in a backend that was never going to load.</summary>
    private void Validate()
    {
        if (Help) return;

        if (Ktls && !OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("--ktls needs Linux (kernel TLS); Windows has no kTLS equivalent.");
        if (Ktls && Which is not (Backend.Auto or Backend.IoUring or Backend.Epoll))
            throw new ArgumentException("--ktls is implemented on the io_uring and epoll backends only; drop the backend override.");
        if (Ktls && VanillaKestrel)
            throw new ArgumentException("--ktls applies to the SocketSet transport; it cannot combine with --kestrel.");
        if (Which is Backend.Iocp or Backend.Rio && !OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("--iocp / --rio need Windows.");
        if (Which is Backend.IoUring or Backend.Epoll && !OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("--io-uring / --epoll need Linux.");
        // --kestrel has no SocketSet bridge to configure, so the DEFAULT simply does not apply to it -
        // silently off. Only an EXPLICIT --byo alongside it is a contradiction worth rejecting. Getting
        // this wrong would throw on every vanilla-Kestrel control leg in every rig, which is the leg all
        // the headline comparisons are measured against.
        if (VanillaKestrel)
        {
            if (ByoPipe && _byoExplicit)
                throw new ArgumentException("--byo configures the SocketSet transport bridge; it cannot combine with --kestrel.");
            ByoPipe = false;
        }
        if ((PageSize > 0 || RecvBufferSize > 0 || WriteBuffers > 0) && VanillaKestrel)
            throw new ArgumentException("--page / --recv-buffer / --write-buffers configure the SocketSet transport; they cannot combine with --kestrel.");
        if (HalfPipe && VanillaKestrel)
            throw new ArgumentException("--half-pipe configures the SocketSet outbound leg; it cannot combine with --kestrel.");
        if (HalfPipe && _byoExplicit && ByoPipe)
            throw new ArgumentException("--half-pipe replaces the outbound leg; it cannot combine with an explicit --byo.");
    }

    /// <summary>
    /// The TLS engine for the SocketSet transport, or null when TLS is off / Kestrel is doing it itself.
    /// Takes the SHARED <paramref name="cert"/> rather than each provider's CreateSelfSignedLoopback
    /// helper, precisely so every leg presents the same key — see <see cref="DemoCertificate"/>.
    /// </summary>
    public TlsProvider? CreateTlsProvider(DemoCertificate cert)
    {
        if (!Tls || VanillaKestrel) return null;

        // Server-only: the provider's client half is unused here, so no trust material is configured.
        if (Ktls || !OperatingSystem.IsWindows())
            return new Tls.OpenSsl.OpenSslTlsProvider(cert.CertPem, cert.KeyPem, kernelOffload: Ktls);

        return CreateSChannelProvider(cert);
    }

    [SupportedOSPlatform("windows")]
    private static TlsProvider CreateSChannelProvider(DemoCertificate cert)
        => new Tls.SChannel.SChannelTlsProvider(cert.Certificate);

    public string Describe()
    {
        string transport = VanillaKestrel
            ? "kestrel-sockets"
            : Which switch
            {
                Backend.Managed => "socketset/managed",
                Backend.Iocp => "socketset/iocp",
                Backend.Rio => "socketset/rio",
                Backend.IoUring => "socketset/io_uring",
                Backend.Epoll => "socketset/epoll",
                // Name what Default actually resolved to, so a run is self-describing in the log.
                _ => $"socketset/auto({Factory.GetType().Name.Replace("Factory", "").ToLowerInvariant()})",
            };

        string tls = (Tls, Ktls, VanillaKestrel) switch
        {
            (false, _, _) => "off",
            (true, true, _) => "ktls (openssl + kernel offload)",
            (true, false, true) => "kestrel/sslstream",
            (true, false, false) => OperatingSystem.IsWindows() ? "schannel (sspi)" : "openssl",
        };

        // Buffer sizes are appended only when overridden, so the strings the bench harnesses match on
        // (transport=, tls=, shards=) are unchanged for every existing leg.
        string bufs = (PageSize > 0 ? $" page={PageSize}" : "")
                    + (RecvBufferSize > 0 ? $" recvbuf={RecvBufferSize}" : "")
                    + (WriteBuffers > 0 ? $" writebufs={WriteBuffers}" : "")
                    // Always state the bridge, in both directions. It used to appear only when ON, so
                    // "classic" was the ABSENCE of a string - fine while byo was opt-in, useless once it
                    // is the default, and a harness gating on absence cannot tell "classic" from "an
                    // older build that had no byo at all".
                    + (ByoPipe ? " byo=pipe" : " byo=off")
                    + (PipeSegment > 0 ? $" pipeseg={PipeSegment}" : "")
                    + (PipePinned ? " pipepinned=1" : "")
                    // House rule: trust the banner, not the flag. A half-pipe run MUST say so, or an A/B
                    // where the flag silently did nothing would measure identically to one that worked.
                    // Also report the drain mode (SS_HALF_DRAIN env), so the inline-vs-pool A/B is banner-gated.
                    + (HalfPipe ? $" half-pipe=1 drain={(Environment.GetEnvironmentVariable("SS_HALF_DRAIN") == "pool" ? "pool" : "inline")}" : "");

        return VanillaKestrel
            ? $"transport={transport} tls={tls} port={Port}"
            : $"transport={transport} tls={tls} shards={Shards} pin={Pin} port={Port}{bufs}";
    }

    public static void PrintUsage()
    {
        Console.WriteLine("usage: AspNetDemo [transport] [tls] [options]");
        Console.WriteLine();
        Console.WriteLine("  transport (default: the SocketSet transport, backend auto-detected)");
        Console.WriteLine("    --kestrel        vanilla Kestrel sockets — the control leg (alias: --default-transport)");
        Console.WriteLine("    --managed        SocketSet's portable managed-socket backend");
        Console.WriteLine("    --iocp / --rio   force a Windows backend");
        Console.WriteLine("    --io-uring       force the Linux io_uring backend");
        Console.WriteLine("    --epoll          force the Linux epoll backend (io_uring's fallback)");
        Console.WriteLine();
        Console.WriteLine("  tls (default: off)");
        Console.WriteLine("    --tls            terminate TLS in the transport (SChannel on Windows, OpenSSL on Linux),");
        Console.WriteLine("                     or in Kestrel's own SslStream when combined with --kestrel");
        Console.WriteLine("    --ktls           as --tls but offloading bulk crypto to the kernel (Linux + io_uring)");
        Console.WriteLine();
        Console.WriteLine("  options");
        Console.WriteLine("    --shards N       SocketSet worker threads (default 2, or $SS_SHARDS)");
        Console.WriteLine("    --pin/--no-pin   pin worker threads to cores (default off, or $SS_PIN=1)");
        Console.WriteLine("    --port N         listen port (default 5080)");
        Console.WriteLine("    --page N         send/write page size (default 4096). RIO cannot scatter-gather, so");
        Console.WriteLine("                     one send is one page and this is its only lever on big responses");
        Console.WriteLine("    --recv-buffer N  per-socket receive buffer (default: follows --page). Keep this SMALL:");
        Console.WriteLine("                     there is one per socket, so it multiplies by sockets-per-shard");
        Console.WriteLine("    --write-buffers N  write buffers per shard (default 1024). Slab = this x --page, so");
        Console.WriteLine("                     a big page wants fewer; running dry currently CLOSES connections");
        Console.WriteLine("    --byo            BYO-buffer bridge: hand Kestrel's own pipes to the transport");
        Console.WriteLine("                     (ctx.UsePipe) instead of copying inbound + pumping outbound here.");
        Console.WriteLine("                     ON BY DEFAULT — parity with vanilla Kestrel at 256KB and +14.2%");
        Console.WriteLine("                     at 1MB, where the classic bridge is -60.3% and -52.6%");
        Console.WriteLine("    --classic        turn it off (alias --no-byo). The classic bridge is the control");
        Console.WriteLine("                     every zero-copy claim is measured against");
        Console.WriteLine("    --half-pipe      EXPERIMENTAL: replace the outbound leg with a CycleBuffer-backed");
        Console.WriteLine("                     PipeWriter that drains to Connection.Send on Kestrel's flush thread");
        Console.WriteLine("                     (no pump, no hop). Copies on send; wins small payloads, not 256KB.");
        Console.WriteLine("                     Turns BYO off. See TODO 'Two half-pipes' + AspNetDemo/RESULTS.md.");
        Console.WriteLine();
        Console.WriteLine("  The TLS certificate is a throwaway self-signed one for localhost, so clients must");
        Console.WriteLine("  skip verification: curl -k https://127.0.0.1:5080/plaintext");
    }
}
