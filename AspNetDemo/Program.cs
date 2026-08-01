using System.Net;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SocketSets.AspNet;

DemoConfig cfg;
try
{
    cfg = DemoConfig.Parse(args);
}
catch (Exception ex) when (ex is ArgumentException or PlatformNotSupportedException)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine();
    DemoConfig.PrintUsage();
    return 1;
}
if (cfg.Help) { DemoConfig.PrintUsage(); return 0; }

// ONE certificate for whichever TLS leg runs (see DemoCertificate): the point of this demo is comparing
// transports, so the certificate must not be a variable.
using var cert = cfg.Tls ? DemoCertificate.Create() : null;

// NOTE: our flags are deliberately NOT forwarded to CreateBuilder — the command-line configuration
// provider treats a bare "--flag" as a key expecting a value and throws on it.
var builder = WebApplication.CreateBuilder();

// There is no appsettings.json here, so the default minimum level is Information — which makes
// Microsoft.AspNetCore.Hosting.Diagnostics write "Request starting"/"Request finished" for EVERY request.
// That is a per-request console write behind a lock, and it dominates everything this demo exists to
// measure: it caps the app at ~2k rps regardless of transport (measured 2026-07-26 — the bare SocketSet
// HTTP responder does ~270k rps on the same box). Warnings and errors still surface.
builder.Logging.SetMinimumLevel(LogLevel.Warning);

if (cfg.HttpSys)
{
    // http.sys — the OS's own kernel-mode HTTP stack, and NOT a Kestrel transport: it replaces the whole
    // server, so none of the Kestrel/SocketSet configuration below applies to it. It is here as the
    // strongest available "the OS already does this" control: request parsing, connection management and
    // response framing all happen in kernel mode, which is a different shape of answer from Kestrel's
    // user-mode loop and from ours.
    //
    // Needs NO elevation and no netsh setup for this prefix, verified on this box: a non-admin process
    // binds http://localhost:<port>/ through http.sys successfully. That is worth stating because the
    // usual folklore is that http.sys always needs `netsh http add urlacl`, which would have made this
    // leg admin-only and therefore useless in an unattended rig.
    builder.WebHost.UseHttpSys(o =>
    {
        // BOTH explicit hosts, and that is not belt-and-braces. http.sys routes on the request's Host
        // header against the registered prefix, so a server bound only to `localhost` answers 400 to
        // http://127.0.0.1:<port>/ — and every bench rig in this repo requests 127.0.0.1. Registering one
        // prefix would have produced a leg that starts, banners correctly, and fails every request.
        //
        // The wildcard forms (`http://+:<port>/`, `http://*:<port>/`) are what the folklore warns about
        // and it is right: both fail with "Access is denied" unelevated, verified on this box. Explicit
        // hosts do not. That difference is the whole reason this leg can run in an unattended rig.
        o.UrlPrefixes.Add($"http://localhost:{cfg.Port}/");
        o.UrlPrefixes.Add($"http://127.0.0.1:{cfg.Port}/");
        // Match the other legs' shape as closely as http.sys allows. It has no per-listener protocol
        // switch equivalent to Kestrel's HttpProtocols.Http1; over plaintext it speaks HTTP/1.1 anyway,
        // which is what keeps this comparable.
        o.MaxConnections = null;      // no artificial cap; the other legs have none either
        o.MaxAccepts = Environment.ProcessorCount * 2;
    });
}
else
{
    builder.WebHost.ConfigureKestrel(o =>
    {
        o.Listen(IPAddress.Loopback, cfg.Port, lo =>
        {
            // Pin every configuration to HTTP/1.1. Otherwise the TLS legs could negotiate HTTP/2 via ALPN
            // while the plaintext legs stayed on 1.1, and the comparison would be measuring two protocols.
            lo.Protocols = HttpProtocols.Http1;
            if (cfg.VanillaKestrel && cfg.Tls) lo.UseHttps(cert!.Certificate); // Kestrel's own SslStream leg
        });
    });
}

if (!cfg.VanillaKestrel && !cfg.HttpSys)
{
    // Replace Kestrel's socket transport with SocketSet (the SocketSet.AspNetCore library). When TLS is on
    // it is terminated DOWN HERE, in the transport (SChannel/SSPI on Windows, OpenSSL — optionally kTLS — on
    // Linux), so Kestrel's HTTP stack sees plaintext and never constructs an SslStream. The demo only maps
    // its A/B flags onto the library's options.
    builder.UseSocketSet(o => cfg.ApplyTo(o, cert));
}

var app = builder.Build();
// The library registers this singleton; null on the vanilla-Kestrel leg (no SocketSet transport).
var metrics = app.Services.GetService<SocketSetTransportMetrics>();

app.MapGet("/", () => "Hello from SocketSet — ASP.NET Core running its HTTP stack over a SocketSet transport!\n");
app.MapGet("/ping", () => Results.Json(new { ok = true }));
app.MapGet("/plaintext", () => Results.Text("OK")); // minimal — isolates transport cost from app work

// Exercise the inbound path with a real request body.
app.MapPost("/echo", async (HttpRequest req) =>
{
    using var ms = new MemoryStream();
    await req.Body.CopyToAsync(ms);
    return Results.Text($"echoed {ms.Length} bytes\n");
});

// Inbound-only: read (and discard) the whole request body, then a tiny response. Isolates the RECEIVE
// path (the transport's inbound copy, or its absence with zero-copy receive) from any outbound cost.
app.MapPost("/drain", async (HttpRequest req) =>
{
    await req.Body.CopyToAsync(Stream.Null);
    return Results.Text("ok\n");
});

// Exercise multi-segment outbound (a response bigger than one buffer/chunk).
app.MapGet("/big", (int n = 100_000) => Results.Text(new string('x', n)));

// Benchmark endpoint: same as /big but served from a PRE-RENDERED byte[] per size. /big allocates a
// fresh n-char string and UTF8-encodes it on every request, which at the sizes and rates used for a
// message-size sweep measures the allocator and the GC rather than the transport.
var payloads = new System.Collections.Concurrent.ConcurrentDictionary<int, byte[]>();
app.MapGet("/payload", (int n = 1024) =>
{
    byte[] body = payloads.GetOrAdd(Math.Clamp(n, 1, 8 * 1024 * 1024), static size =>
    {
        var b = new byte[size];
        b.AsSpan().Fill((byte)'x');
        return b;
    });
    return Results.Bytes(body, "text/plain");
});

// What is actually running — hit this first to confirm the leg under test is the leg you meant.
app.MapGet("/config", (HttpContext http) => Results.Json(new
{
    config = cfg.Describe(),
    // What the BACKEND chose, which is not always what was asked for: unset sizes are "backend chooses"
    // sentinels, so RIO runs a 64KB page nobody typed. Gate on this, not on the flags you passed.
    geometry = metrics?.ResolvedGeometry,
    certificate = cert?.Describe(),
    isHttps = http.Request.IsHttps,
    protocol = http.Request.Protocol,
    alpn = http.Features.Get<ITransportTlsFeature>()?.NegotiatedProtocol,
}));

app.MapGet("/stats", () => Results.Json(new
{
    accepts = metrics?.Accepts ?? 0,
    closes = metrics?.Closes ?? 0,
    closedEmpty = metrics?.ClosedEmpty ?? 0,
    writeFail = metrics?.WriteFail ?? 0,
    sendFalse = metrics?.SendFalse ?? 0,
    // GC / memory, for the allocation-and-RSS axis (the half-pipe's "leaner machinery" claim). Read these
    // before and after a fixed load and diff: gen0 collections and allocatedBytes are the alloc signal;
    // rssBytes/gcHeapBytes are the footprint signal. GetTotalAllocatedBytes is process-wide, so drive ONE
    // leg per process (the bench starts a fresh server per leg, so that holds).
    gen0 = GC.CollectionCount(0),
    gen1 = GC.CollectionCount(1),
    gen2 = GC.CollectionCount(2),
    allocatedBytes = GC.GetTotalAllocatedBytes(),
    gcHeapBytes = GC.GetTotalMemory(false),
    rssBytes = Environment.WorkingSet,
}));

Console.WriteLine($"[aspnet demo] {cfg.Describe()}");
if (cert is not null) Console.WriteLine($"[aspnet demo] certificate: {cert.Describe()} (self-signed — clients need curl -k)");
Console.WriteLine($"[aspnet demo] listening on {cfg.Scheme}://127.0.0.1:{cfg.Port}");

app.Run();
return 0;
