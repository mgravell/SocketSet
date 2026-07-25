using SocketSets.AspNet;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.DependencyInjection.Extensions;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5080");

// Replace Kestrel's default socket transport with the SocketSet io_uring transport.
// (--default-transport keeps Kestrel's built-in sockets, as an A/B control.)
if (!args.Contains("--default-transport"))
{
    builder.Services.RemoveAll<IConnectionListenerFactory>();
    builder.Services.AddSingleton<IConnectionListenerFactory, SocketSetTransportFactory>();
}

var app = builder.Build();

app.MapGet("/", () => "Hello from SocketSet — ASP.NET Core running its HTTP stack over an io_uring transport!\n");
app.MapGet("/ping", () => Results.Json(new { ok = true, transport = "socketset-io_uring" }));

// Exercise the inbound path with a real request body.
app.MapPost("/echo", async (HttpRequest req) =>
{
    using var ms = new MemoryStream();
    await req.Body.CopyToAsync(ms);
    return Results.Text($"echoed {ms.Length} bytes\n");
});

// Exercise multi-segment outbound (a response bigger than one buffer/chunk).
app.MapGet("/big", (int n = 100_000) => Results.Text(new string('x', n)));

app.MapGet("/stats", () => Results.Json(new
{
    accepts = SocketSets.AspNet.SocketSetConnectionListener.Accepts,
    closes = SocketSets.AspNet.SocketSetConnectionListener.Closes,
    closedEmpty = SocketSets.AspNet.SocketSetConnectionListener.ClosedEmpty,
    writeFail = SocketSets.AspNet.SocketSetConnectionListener.WriteFail,
    sendFalse = SocketSets.AspNet.SocketSetConnectionListener.SendFalse,
}));

app.Run();
