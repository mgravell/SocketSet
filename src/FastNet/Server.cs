using System.Runtime.InteropServices;
using FastNet.Fallback;
using FastNet.Transport;

namespace FastNet;

internal static class Program
{
    // Hard-coded abstract-namespace UDS name (the leading NUL is added at bind).
    // Shared by server and, conceptually, the bench's -uds mode.
    internal const string AbstractName = "fastnet-echo";

    private static void Main(string[] args)
    {
        // Args: first standalone number is the port. "-socket" forces the plain
        // Socket baseline even on Linux; "-shards N" sets the io_uring shard
        // count (default = core count); "-pin" pins each shard to a core;
        // "-uds" listens on the abstract Unix socket instead of TCP — the
        // loopback-proxy front end: no port churn, no TIME_WAIT, no socket file.
        bool forceSocket = false;
        bool ring = false;
        bool pin = false;
        bool uds = false;
        int port = 8080;
        int shards = Environment.ProcessorCount;
        bool portSet = false;
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a == "-socket") forceSocket = true;
            else if (a == "-ring") ring = true;
            else if (a == "-pin") pin = true;
            else if (a == "-uds") uds = true;
            else if (a == "-shards" && i + 1 < args.Length && int.TryParse(args[i + 1], out var n)) { shards = Math.Max(1, n); i++; }
            else if (!portSet && int.TryParse(a, out var p)) { port = p; portSet = true; }
        }

        string? udsName = uds ? AbstractName : null;

        const int maxConnections = 1024;
        const int bufferSize = 16 * 1024;

        if (forceSocket)
        {
            RunSocket(port, bufferSize, udsName);
        }
        else if (ring && RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // SKETCH backend: multishot recv + provided buffer ring (single shard).
            using var server = new RingEchoServer(port, maxConnections, bufferSize, udsName: udsName);
            server.Initialize();

            Console.CancelKeyPress += (_, e) => { e.Cancel = true; server.Stop(); };
            Console.WriteLine("[Boot] io_uring ring-buffer echo server (sketch) — Ctrl+C to stop.");
            server.Run();
            Console.WriteLine("[Boot] stopped.");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            using var server = new ShardedEchoServer(port, shards, maxConnections, bufferSize, pin, udsName);
            server.Initialize();

            Console.CancelKeyPress += (_, e) => { e.Cancel = true; server.Stop(); };
            Console.WriteLine($"[Boot] io_uring echo server — {shards} shard(s){(pin ? ", pinned" : "")} — Ctrl+C to stop.");
            server.Run();
            Console.WriteLine("[Boot] stopped.");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw new PlatformNotSupportedException(
                "RIO backend not implemented yet — see WinRio.RioEngine. io_uring is the reference transport.");
        }
        else
        {
            // No RIO/io_uring here — fall back to the portable Socket server.
            RunSocket(port, bufferSize, udsName);
        }
    }

    private static void RunSocket(int port, int bufferSize, string? udsName)
    {
        using var server = new SocketEchoServer(port, bufferSize, udsName);
        server.Initialize();

        Console.CancelKeyPress += (_, e) => { e.Cancel = true; server.Stop(); };
        Console.WriteLine("[Boot] Socket echo server — Ctrl+C to stop.");
        server.Run();
        Console.WriteLine("[Boot] stopped.");
    }
}
