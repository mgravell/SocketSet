using System;
using System.Runtime.InteropServices;
using FastNet.Transport;

namespace FastNet;

internal static class Program
{
    private static void Main(string[] args)
    {
        int port = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 8080;
        const int maxConnections = 1024;
        const int bufferSize = 16 * 1024;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            using var server = new EchoServer(port, maxConnections, bufferSize);
            server.Initialize();

            Console.CancelKeyPress += (_, e) => { e.Cancel = true; server.Stop(); };
            Console.WriteLine("[Boot] io_uring echo server — Ctrl+C to stop.");
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
            throw new PlatformNotSupportedException(
                "SAEA fallback not implemented yet — see Fallback.SaeaEngine.");
        }
    }
}
