// Embedded Garnet on the SocketSet transport — the GarnetDemo plays the role AspNetDemo plays for
// Kestrel: the smallest host that lets the rigs point at a real server, with a banner they can gate on.
//
// usage: GarnetDemo [--port N] [--backend io-uring|epoll|managed] [--shards N] [--stock]
//   --stock hosts Garnet's OWN GarnetServerTcp instead (the SAEA layer) on the same options, so a
//   stock-vs-socketset A/B is one flag on one binary — the application-held-constant discipline again.
using System.Net;
using Garnet;
using Garnet.server;
using SocketSets;
using SocketSets.Garnet;

int port = 6390, shards = 8;
string backend = "io-uring";
bool stock = false;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], out var p): port = p; i++; break;
        case "--shards" when i + 1 < args.Length && int.TryParse(args[i + 1], out var s): shards = s; i++; break;
        case "--backend" when i + 1 < args.Length: backend = args[++i]; break;
        case "--stock": stock = true; break;
        default: Console.Error.WriteLine($"unknown argument: {args[i]}"); return 1;
    }
}

var endpoint = new IPEndPoint(IPAddress.Loopback, port);
var garnetOpts = new GarnetServerOptions { EndPoints = [endpoint] };

IGarnetServer[]? servers = null;
if (!stock)
{
    var factory = backend switch
    {
        "io-uring" => SocketSetFactory.IoUring,
        "epoll" => SocketSetFactory.Epoll,
        "managed" => SocketSetFactory.Managed,
        _ => throw new ArgumentException($"unknown backend '{backend}'"),
    };
    servers = [new SocketSetGarnetServer(endpoint, new SocketSetOptions { Factory = factory, Shards = shards })];
}

using var server = new GarnetServer(garnetOpts, loggerFactory: null, servers: servers);
server.Start();

// TRUST THE BANNER: the rigs gate on this line, not on the flags they passed.
Console.WriteLine($"[garnet-demo] transport={(stock ? "garnet-saea" : $"socketset/{backend} shards={shards}")} port={port}");
Console.WriteLine("ready");
Thread.Sleep(Timeout.Infinite);
return 0;
