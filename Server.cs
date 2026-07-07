using System;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using FastNet.Abstraction;
using FastNet.LinuxUring;
using FastNet.WinRio;

namespace FastNet;

class Program
{
    static void Main(string[] args)
    {
        IOEngine engine;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Console.WriteLine("[Boot] Windows environment detected. Binding RIO...");
            engine = new RioEngine();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            Console.WriteLine("[Boot] Linux environment detected. Binding io_uring...");
            engine = new IoUringEngine();
        }
        else
        {
            Console.WriteLine("[Boot] Unhandled platform (macOS/BSD). Falling back to Managed SAEA...");
            engine = new ManagedEngine();
        }

        // The exact same engine initialization sequence continues down here
        int bufferSize = 4096;
        byte[] megaBuffer = new byte[bufferSize * 100];
            
        engine.Initialize(new IPEndPoint(IPAddress.Any, 8080), 100, bufferSize);
        engine.RegisterBuffers(megaBuffer);
            
        // Core application polling loop remains 100% identical...

        bool running = true;
        while (running)
        {
            // Engine polls completion frames from the underlying kernel queues
            engine.PollCompletions((result) =>
            {
                if (!result.Success) return;

                switch (result.Operation)
                {
                    case OpType.Receive:
                        if (result.BytesTransferred > 0)
                        {
                            // Adjust slice boundaries to match received data payload size
                            var echoSlice = result.Slice;
                            echoSlice.Length = result.BytesTransferred;

                            // Direct echo pass-through: write back out using the exact same memory slice
                            engine.PostSend(result.SocketContext, echoSlice);
                        }

                        break;

                    case OpType.Send:
                        // Echo chunk sent out completely. Re-arm buffer slice back to read posture.
                        var nextReceiveSlice = result.Slice;
                        nextReceiveSlice.Length = bufferSize;
                        engine.PostReceive(result.NativeHandle, nextReceiveSlice);
                        break;
                }
            });

            Thread.Sleep(1); // Reduce CPU load in this simple polling example
        }
    }
}