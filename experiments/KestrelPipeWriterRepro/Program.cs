using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;

// ---------------------------------------------------------------------------
// Minimal, self-contained repro: how Kestrel drives a connection-transport PipeWriter.
//
// It stands up a KestrelServer over a fake in-memory transport that feeds ONE canned
// HTTP/1.1 request and exposes an INSTRUMENTED PipeWriter as Transport.Output. The writer
// records every GetMemory / Advance / FlushAsync call and checks one thing:
//
//   IBufferWriter<T> contract (from the .NET docs for IBufferWriter<T>.Advance):
//     "You must request a new buffer after calling Advance(Int32) to continue writing more
//      data; you cannot write to a previously acquired buffer."
//
// i.e. every Advance(>0) must be preceded by its own GetSpan/GetMemory, and you may not keep
// writing into an earlier buffer after you have Advanced past it.
//
// Observed with the ASP.NET Core minimal API below returning a 5-byte body: Kestrel calls
// GetMemory(0) ONCE, Advance(<headers>), then writes the response BODY into that same retained
// buffer past the header bytes and Advance(<body>) -- a second Advance with no GetMemory in
// between. A PipeWriter whose GetMemory returns a distinct buffer each call (as the contract
// permits) loses the body.
//
// No SocketSet, no CycleBuffer, no external transport -- just Kestrel + this file.
// ---------------------------------------------------------------------------

Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
Console.WriteLine($"[repro] runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");

// Each connection gets its own SpyPipeWriter; we report the FIRST one (Kestrel may bind more than one
// default endpoint, so more than one one-shot connection can occur -- reporting one keeps the trace clean).
var firstSpy = new TaskCompletionSource<SpyPipeWriter>(TaskCreationOptions.RunContinuationsAsynchronously);

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:5000");
builder.WebHost.ConfigureServices(services =>
{
    services.AddSingleton<IConnectionListenerFactory>(new OneShotTransportFactory(firstSpy));
});

var app = builder.Build();
// Same shape as the transport demo's /payload?n=5: a fixed small byte[] body.
app.MapGet("/t", () => Results.Bytes(Encoding.ASCII.GetBytes("xxxxx"), "text/plain"));

Console.WriteLine("[repro] starting host...");
await app.StartAsync();
Console.WriteLine("[repro] host started; awaiting the one-shot connection...");
var done = await Task.WhenAny(firstSpy.Task, Task.Delay(5000));
if (done != firstSpy.Task) { Console.WriteLine("[repro] timed out (5s) with no connection"); Environment.Exit(2); }
await Task.Delay(200); // let the response finish writing

firstSpy.Task.Result.Report();
// Skip graceful StopAsync (it can block on the parked accept); we have what we came for.
Environment.Exit(0);

// ===========================================================================
// Instrumented transport PipeWriter: records the call sequence and the verdict.
// ===========================================================================
sealed class SpyPipeWriter : PipeWriter
{
    // Conformant backing: one growing buffer, GetMemory returns space at the write head (exactly what
    // System.IO.Pipelines.Pipe does within a segment), so Kestrel produces a byte-correct response and
    // we can print it. Independently we flag the contract-relevant event: an Advance(>0) that was NOT
    // preceded by its own GetSpan/GetMemory. Such an Advance means the caller kept writing into a buffer
    // it had already advanced past -- which the IBufferWriter<T> docs disallow ("you must request a new
    // buffer after calling Advance ... you cannot write to a previously acquired buffer") and which
    // silently drops data on any PipeWriter whose GetMemory hands back distinct buffers (the docs also say
    // "no guarantee that successive calls will return the same buffer").
    private readonly List<string> _log = new();
    private byte[] _out = new byte[1 << 16];
    private int _head;                     // committed length
    private int _leaseStart = -1;          // head at the time of the current outstanding GetMemory/GetSpan
    private bool _leasedSinceAdvance;      // was there a GetMemory/GetSpan since the last Advance?
    private int _violations;

    private void Ensure(int extra)
    {
        if (_head + extra <= _out.Length) return;
        int n = _out.Length * 2;
        while (_head + extra > n) n *= 2;
        Array.Resize(ref _out, n);
    }

    public override Memory<byte> GetMemory(int sizeHint = 0)
    {
        Ensure(Math.Max(sizeHint, 1));
        _leaseStart = _head;
        _leasedSinceAdvance = true;
        _log.Add($"GetMemory(hint={sizeHint}) -> {_out.Length - _head}B at head={_head}");
        return _out.AsMemory(_head);
    }

    public override Span<byte> GetSpan(int sizeHint = 0)
    {
        Ensure(Math.Max(sizeHint, 1));
        _leaseStart = _head;
        _leasedSinceAdvance = true;
        _log.Add($"GetSpan(hint={sizeHint}) -> {_out.Length - _head}B at head={_head}");
        return _out.AsSpan(_head);
    }

    public override void Advance(int bytes)
    {
        bool legal = _leasedSinceAdvance || bytes == 0;
        if (!legal) _violations++;
        string note = _leasedSinceAdvance
            ? $"(wrote into the buffer leased at head={_leaseStart})"
            : "*** no GetSpan/GetMemory since the last Advance -- wrote past the previous Advance ***";
        _log.Add($"Advance({bytes})  {note}");
        _head += bytes;
        _leasedSinceAdvance = false;
    }

    public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
    {
        _log.Add($"FlushAsync  (head={_head})");
        return new ValueTask<FlushResult>(new FlushResult(isCanceled: false, isCompleted: false));
    }

    public override void CancelPendingFlush() { }
    public override void Complete(Exception? exception = null) => _log.Add("Complete");
    public override bool CanGetUnflushedBytes => true;
    public override long UnflushedBytes => _head;

    public void Report()
    {
        Console.WriteLine("=== Kestrel -> transport PipeWriter call sequence ===");
        foreach (var l in _log) Console.WriteLine("  " + l);
        Console.WriteLine();
        Console.WriteLine($"Assembled response ({_head}B), contiguous backing so it is byte-correct:");
        Console.WriteLine("----");
        Console.WriteLine(Encoding.ASCII.GetString(_out, 0, _head).Replace("\r", "\\r").Replace("\n", "\\n\n"));
        Console.WriteLine("----");
        Console.WriteLine();
        Console.WriteLine($"VERDICT: {_violations} Advance(>0) call(s) with NO preceding GetSpan/GetMemory.");
        Console.WriteLine(_violations > 0
            ? "  => Kestrel wrote past an Advance into a previously-acquired buffer. This backing is\n" +
              "     contiguous so nothing is lost here, but IBufferWriter<T> allows GetMemory to return a\n" +
              "     DIFFERENT buffer each call; such a writer loses the bytes written after that Advance."
            : "  => No violation observed.");
    }
}

// ===========================================================================
// A fake connection transport that yields exactly one connection carrying a canned request.
// ===========================================================================
sealed class OneShotTransportFactory(TaskCompletionSource<SpyPipeWriter> firstSpy) : IConnectionListenerFactory
{
    public ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken ct = default)
        => new(new OneShotListener(endpoint, firstSpy));
}

sealed class OneShotListener : IConnectionListener
{
    private readonly TaskCompletionSource<SpyPipeWriter> _firstSpy;
    private int _served;
    public OneShotListener(EndPoint endpoint, TaskCompletionSource<SpyPipeWriter> firstSpy)
    { EndPoint = endpoint; _firstSpy = firstSpy; }

    public EndPoint EndPoint { get; }

    public async ValueTask<ConnectionContext?> AcceptAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _served, 1) == 1)
        {
            // One connection per listener; park so Kestrel's accept loop doesn't spin.
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return null;
        }
        return new OneShotConnection(_firstSpy);
    }

    public ValueTask UnbindAsync(CancellationToken ct = default) => default;
    public ValueTask DisposeAsync() => default;
}

sealed class OneShotConnection : ConnectionContext, IDuplexPipe
{
    private readonly Pipe _requestPipe;
    private readonly SpyPipeWriter _spy;

    public OneShotConnection(TaskCompletionSource<SpyPipeWriter> firstSpy)
    {
        _spy = new SpyPipeWriter();
        firstSpy.TrySetResult(_spy); // publish this connection's writer as the one to report
        _requestPipe = new Pipe();
        // Feed one canned HTTP/1.1 request, then complete so Kestrel serves it and closes.
        var req = Encoding.ASCII.GetBytes("GET /t HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");
        _requestPipe.Writer.Write(req);
        _requestPipe.Writer.Complete();

        Transport = this;
        Features = new FeatureCollection();
        Items = new Dictionary<object, object?>();
        ConnectionId = "one-shot";
        ConnectionClosed = _closed.Token;
    }

    private readonly CancellationTokenSource _closed = new();

    public PipeReader Input => _requestPipe.Reader;
    public PipeWriter Output => _spy;

    public override IDuplexPipe Transport { get; set; }
    public override string ConnectionId { get; set; }
    public override IFeatureCollection Features { get; }
    public override IDictionary<object, object?> Items { get; set; }
    public override CancellationToken ConnectionClosed { get; set; }

    public override void Abort() => Abort(new ConnectionAbortedException());
    public override void Abort(ConnectionAbortedException abortReason) => _closed.Cancel();

    public override ValueTask DisposeAsync() => default;
}
