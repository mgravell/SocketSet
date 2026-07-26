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

if (!cfg.VanillaKestrel)
{
    // Replace Kestrel's socket transport with SocketSet. When TLS is on it is terminated DOWN HERE, in
    // the transport (SChannel/SSPI on Windows, OpenSSL — optionally kTLS — on Linux), so Kestrel's HTTP
    // stack sees plaintext and never constructs an SslStream.
    builder.Services.RemoveAll<IConnectionListenerFactory>();
    builder.Services.AddSingleton(cfg);
    builder.Services.AddSingleton(new TransportTlsProvider(cfg.CreateTlsProvider(cert!)));
    builder.Services.AddSingleton<IConnectionListenerFactory, SocketSetTransportFactory>();
}

var app = builder.Build();

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
    certificate = cert?.Describe(),
    isHttps = http.Request.IsHttps,
    protocol = http.Request.Protocol,
    alpn = http.Features.Get<ITransportTlsFeature>()?.NegotiatedProtocol,
}));

app.MapGet("/stats", () => Results.Json(new
{
    accepts = SocketSetConnectionListener.Accepts,
    closes = SocketSetConnectionListener.Closes,
    closedEmpty = SocketSetConnectionListener.ClosedEmpty,
    writeFail = SocketSetConnectionListener.WriteFail,
    sendFalse = SocketSetConnectionListener.SendFalse,
}));

Console.WriteLine($"[aspnet demo] {cfg.Describe()}");
if (cert is not null) Console.WriteLine($"[aspnet demo] certificate: {cert.Describe()} (self-signed — clients need curl -k)");
Console.WriteLine($"[aspnet demo] listening on {cfg.Scheme}://127.0.0.1:{cfg.Port}");

app.Run();
return 0;

/// <summary>DI carrier for the transport's TLS engine (null = plaintext). A wrapper rather than the
/// provider itself so the container can resolve "no TLS" without a null registration.</summary>
internal sealed record TransportTlsProvider(SocketSets.Tls.TlsProvider? Provider);
