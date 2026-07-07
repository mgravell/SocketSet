using System;
using System.Runtime.InteropServices;
using FastNet.Fallback;
using FastNet.Transport;

namespace FastNet;

internal static class Program
{
    private static void Main(string[] args)
    {
        // Args: first standalone number is the port. "-socket" forces the plain
        // Socket baseline even on Linux; "-shards N" sets the io_uring shard
        // count (default = core count); "-pin" pins each shard to a core.
        bool forceSocket = false;
        bool pin = false;
        int port = 8080;
        int shards = Environment.ProcessorCount;
        bool portSet = false;
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a == "-socket") forceSocket = true;
            else if (a == "-pin") pin = true;
            else if (a == "-shards" && i + 1 < args.Length && int.TryParse(args[i + 1], out var n)) { shards = Math.Max(1, n); i++; }
            else if (!portSet && int.TryParse(a, out var p)) { port = p; portSet = true; }
        }

        const int maxConnections = 1024;
        const int bufferSize = 16 * 1024;

        if (forceSocket)
        {
            RunSocket(port, bufferSize);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            using var server = new ShardedEchoServer(port, shards, maxConnections, bufferSize, pin);
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
            RunSocket(port, bufferSize);
        }
    }

    private static void RunSocket(int port, int bufferSize)
    {
        using var server = new SocketEchoServer(port, bufferSize);
        server.Initialize();

        Console.CancelKeyPress += (_, e) => { e.Cancel = true; server.Stop(); };
        Console.WriteLine("[Boot] Socket echo server — Ctrl+C to stop.");
        server.Run();
        Console.WriteLine("[Boot] stopped.");
    }
}
