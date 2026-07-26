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
///               with kernel offload (kTLS: Linux + io_uring only). Vanilla Kestrel does its own HTTPS
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
        if (Ktls && Which is not (Backend.Auto or Backend.IoUring))
            throw new ArgumentException("--ktls is only implemented on the io_uring backend; drop the backend override.");
        if (Ktls && VanillaKestrel)
            throw new ArgumentException("--ktls applies to the SocketSet transport; it cannot combine with --kestrel.");
        if (Which is Backend.Iocp or Backend.Rio && !OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("--iocp / --rio need Windows.");
        if (Which is Backend.IoUring or Backend.Epoll && !OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("--io-uring / --epoll need Linux.");
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

        return VanillaKestrel
            ? $"transport={transport} tls={tls} port={Port}"
            : $"transport={transport} tls={tls} shards={Shards} pin={Pin} port={Port}";
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
        Console.WriteLine();
        Console.WriteLine("  The TLS certificate is a throwaway self-signed one for localhost, so clients must");
        Console.WriteLine("  skip verification: curl -k https://127.0.0.1:5080/plaintext");
    }
}
