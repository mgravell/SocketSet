using System.Buffers;   // BuffersExtensions.Write, for PipeWriter as an IBufferWriter<byte>
using SocketSets;

namespace SmokeTest;

/// <summary>
/// Bidirectional content-verification harness for the ECHO data path (the normal recv→respond road, as
/// opposed to <see cref="SendVerify"/>'s out-of-band <see cref="Connection.Send"/> path). The client
/// streams a known repeating pattern under a bounded window; the server verifies each inbound chunk
/// against the pattern (localizes client→server corruption) then echoes it verbatim; the client verifies
/// every byte it gets back against the same pattern at the running offset (catches corruption, reorder,
/// or truncation anywhere in the loop).
///
/// Byte-exact — unlike the throughput echo (EchoServer), which fills with a constant and only counts
/// bytes, so a silently dropped/duplicated/reordered message reads as "throughput looked fine". This is
/// the both-directions path the RIO commit piggyback (a send commit carries a co-pending recv) relies
/// on; before this it was only stall-tested, never content-tested — which is exactly how the recv-commit
/// bug hid. Run at window=1 (ping/pong) and window&gt;1 (coalesced pipeline) to cover both regimes.
/// </summary>
public sealed class EchoVerify(SocketSetOptions options, long totalBytes, int chunk, int window) : SocketSet(options)
{
    // Largest prime < 2^16: a long non-repeating stride against any power-of-two chunk/page size, so a
    // page-aligned reorder can't alias to a matching pattern byte and slip past the check.
    private const int PatternLen = 65521;
    private readonly byte[] _pattern = MakePattern();

    private long _roundTripped;      // client: bytes verified back
    private int _clientMismatches;   // client-side (full round-trip) byte mismatches
    private int _serverMismatches;   // server-side (inbound) byte mismatches — localizes to the client→server leg

    public long RoundTripped => Interlocked.Read(ref _roundTripped);
    public int ClientMismatches => Volatile.Read(ref _clientMismatches);
    public int ServerMismatches => Volatile.Read(ref _serverMismatches);
    public long Expected => totalBytes;

    private static byte[] MakePattern()
    {
        var p = new byte[PatternLen];
        for (int i = 0; i < PatternLen; i++) p[i] = (byte)(i % 251); // 251 prime → stride relatively prime to 65521
        return p;
    }

    private void FillPattern(Span<byte> dst, long absOffset)
    {
        int j = (int)(absOffset % PatternLen);
        for (int i = 0; i < dst.Length; i++)
        {
            dst[i] = _pattern[j];
            if (++j == PatternLen) j = 0;
        }
    }

    private int CountMismatches(ReadOnlySpan<byte> src, long absOffset)
    {
        int j = (int)(absOffset % PatternLen), mism = 0;
        for (int i = 0; i < src.Length; i++)
        {
            if (src[i] != _pattern[j]) mism++;
            if (++j == PatternLen) j = 0;
        }
        return mism;
    }

    /// <summary>Opt the SERVER half into pipe mode (ctx.UsePipe). Only the server side, so a mismatch is
    /// attributable to the new path rather than to both ends changing at once.</summary>
    public bool PipeMode { get; set; }

    protected override void OnAccept(ref AcceptContext ctx)
    {
        var s = new ServerState();
        ctx.Connection.UserToken = s;
#if NET
        // The byte-exact harness is the ONLY thing that can catch a zero-copy send pointing at the wrong
        // address or resuming a partial write at the wrong offset, so pipe mode has to be reachable from
        // here. It was not until 2026-07-28: EchoVerify is a separate SocketSet from EchoServer, so
        // `--verify-echo --pipe` silently ran the ordinary callback path and verified nothing about pipes.
        if (PipeMode) StartPipeEcho(ref ctx, s);
#endif
    }

#if NET
    private void StartPipeEcho(ref AcceptContext ctx, ServerState s)
    {
        var inbound = new System.IO.Pipelines.Pipe();   // transport writes -> we read
        var outbound = new System.IO.Pipelines.Pipe();  // we write -> transport reads
        ctx.UsePipe(new DuplexPipe(outbound.Reader, inbound.Writer));

        var appIn = inbound.Reader;
        var appOut = outbound.Writer;
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var result = await appIn.ReadAsync().ConfigureAwait(false);
                    var buffer = result.Buffer;
                    if (!buffer.IsEmpty)
                    {
                        // Same verification the callback path does: check the inbound leg against the
                        // pattern, then echo verbatim.
                        foreach (var seg in buffer)
                        {
                            lock (s.Gate)
                            {
                                int m = CountMismatches(seg.Span, s.RecvOffset);
                                if (m != 0) Interlocked.Add(ref _serverMismatches, m);
                                s.RecvOffset += seg.Length;
                            }
                            appOut.Write(seg.Span);
                        }
                        var f = await appOut.FlushAsync().ConfigureAwait(false);
                        if (f.IsCompleted || f.IsCanceled) { appIn.AdvanceTo(buffer.End); break; }
                    }
                    appIn.AdvanceTo(buffer.End);
                    if (result.IsCompleted || result.IsCanceled) break;
                }
            }
            catch { /* torn down mid-echo */ }
            finally
            {
                try { await appIn.CompleteAsync().ConfigureAwait(false); } catch { }
                try { await appOut.CompleteAsync().ConfigureAwait(false); } catch { }
            }
        });
    }

    private sealed class DuplexPipe(System.IO.Pipelines.PipeReader input, System.IO.Pipelines.PipeWriter output)
        : System.IO.Pipelines.IDuplexPipe
    {
        public System.IO.Pipelines.PipeReader Input => input;
        public System.IO.Pipelines.PipeWriter Output => output;
    }
#endif

    protected override void OnConnect(ref ConnectContext ctx)
    {
        var c = new ClientState(totalBytes, chunk, (long)Math.Max(1, window) * chunk);
        ctx.Connection.UserToken = c;
        int n; long sendAt;
        lock (c.Gate) { n = c.Writable(ctx.SendBuffer.Length); sendAt = c.LastOffset; } // prime the pipe
        if (n > 0) { FillPattern(ctx.SendBuffer.Slice(0, n), sendAt); ctx.SendBytes = n; }
    }

    protected override void OnReceive(ref ReceiveContext ctx)
    {
        switch (ctx.Connection.UserToken)
        {
            case ServerState s:
            {
                // Verify the inbound leg against the pattern (localizes client→server corruption), then
                // echo the bytes back verbatim — the buffer already holds them.
                lock (s.Gate)
                {
                    int m = CountMismatches(ctx.Payload, s.RecvOffset);
                    if (m != 0) Interlocked.Add(ref _serverMismatches, m);
                    s.RecvOffset += ctx.PayloadBytes;
                }
                ctx.ResponseBytes = ctx.PayloadBytes;
                break;
            }
            case ClientState c:
            {
                int n;
                long sendAt;
                lock (c.Gate)
                {
                    int m = CountMismatches(ctx.Payload, c.RecvOffset);
                    if (m != 0) Interlocked.Add(ref _clientMismatches, m);
                    c.RecvOffset += ctx.PayloadBytes;
                    Interlocked.Add(ref _roundTripped, ctx.PayloadBytes);

                    if (c.RecvOffset >= c.Target) { ctx.Connection.Close(); return; } // all sent and echoed back
                    n = c.Replied(ctx.RawBuffer.Length);    // a slot freed → kick the pipe only if it stalled
                    sendAt = c.LastOffset;
                }
                if (n > 0) { FillPattern(ctx.RawBuffer.Slice(0, n), sendAt); ctx.ResponseBytes = n; }
                break;
            }
        }
    }

    protected override void OnWrite(ref WriteContext ctx)
    {
        if (ctx.Connection.UserToken is not ClientState c) return;
        int n;
        long sendAt;
        lock (c.Gate) { n = c.Writable(ctx.SendBuffer.Length); sendAt = c.LastOffset; }
        if (n > 0) { FillPattern(ctx.SendBuffer.Slice(0, n), sendAt); ctx.SendBytes = n; }
    }

    private sealed class ServerState
    {
        public readonly object Gate = new();
        public long RecvOffset;
    }

    /// <summary>Per-client send-window bookkeeping. At most one write is in flight (transport constraint);
    /// at most <c>windowBytes</c> bytes are outstanding (sent, not yet echoed back). The lock makes the
    /// "pipe idle + window room → claim the next chunk" decision atomic against the managed backend, whose
    /// OnWrite/OnReceive for one connection can land on different thread-pool threads.</summary>
    private sealed class ClientState(long target, int chunk, long windowBytes)
    {
        public readonly object Gate = new();
        public readonly long Target = target;
        public long SendOffset;   // bytes handed to the transport
        public long RecvOffset;   // bytes verified back
        public long LastOffset;   // absolute offset of the chunk the most recent claim took
        private bool _writeIdle = true;

        // Claim the next chunk (start = LastOffset), advancing SendOffset. Returns bytes to send, 0 if none.
        private int Claim(int bufLen)
        {
            long room = windowBytes - (SendOffset - RecvOffset);
            long remaining = Target - SendOffset;
            if (remaining <= 0 || room <= 0) { _writeIdle = true; return 0; }
            int n = (int)Math.Min(Math.Min((long)chunk, remaining), Math.Min(room, bufLen));
            LastOffset = SendOffset;
            SendOffset += n;
            _writeIdle = false;
            return n;
        }

        /// <summary>Write pipe is free (connect / write-complete) → claim the next chunk if the window has
        /// room. Leaves the pipe idle (returns 0) when full or the quota is spent.</summary>
        public int Writable(int bufLen) => Claim(bufLen);

        /// <summary>A reply freed a window slot. Only claim here if the pipe went idle at the window limit
        /// — otherwise a write is already in flight and OnWrite will issue the next, and sending here too
        /// would put two writes on the wire (reorder). Mirrors EchoServer's writeIdle handoff.</summary>
        public int Replied(int bufLen) => _writeIdle ? Claim(bufLen) : 0;
    }
}
