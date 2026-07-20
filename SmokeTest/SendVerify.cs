using System.Buffers;
using SocketSets;

namespace SmokeTest;

/// <summary>
/// Correctness harness for out-of-band <see cref="Connection.Send"/> (including the multi-page
/// writev path and the ReadOnlySequence overload). The server captures each accepted connection and
/// pushes a large known pattern out-of-band — once as a contiguous span, once as a multi-segment
/// sequence — from a non-IO thread. The client verifies every received byte against the pattern and
/// counts mismatches, so reordering, truncation, or corruption in the scatter-gather path shows up.
/// </summary>
public sealed class SendVerify(SocketSetOptions options, int payloadLen, int segSize) : SocketSet(options)
{
    private readonly byte[] _pattern = MakePattern(payloadLen);
    private long _received;
    private int _mismatches;
    private volatile Connection? _serverConn;

    public long Received => Interlocked.Read(ref _received);
    public int Mismatches => Volatile.Read(ref _mismatches);
    public long Expected => 2L * payloadLen; // one span send + one sequence send
    public Connection? ServerConn => _serverConn;

    private static readonly object ServerToken = new();

    private static byte[] MakePattern(int len)
    {
        var p = new byte[len];
        for (int i = 0; i < len; i++) p[i] = (byte)(i % 251); // 251 prime → long non-repeating stride
        return p;
    }

    protected override void OnAccept(ref AcceptContext ctx)
    {
        ctx.Connection.UserToken = ServerToken;
        ctx.CloseInput(); // server only sends
        _serverConn = ctx.Connection;
    }

    protected override void OnReceive(ref ReceiveContext ctx)
    {
        if (ReferenceEquals(ctx.Connection.UserToken, ServerToken)) return; // server side never reads

        // Client: verify the payload against the pattern at the current running offset.
        var payload = ctx.Payload;
        long baseIdx = Interlocked.Read(ref _received);
        int mism = 0;
        for (int i = 0; i < payload.Length; i++)
        {
            if (payload[i] != _pattern[(int)((baseIdx + i) % _pattern.Length)]) mism++;
        }
        if (mism != 0) Interlocked.Add(ref _mismatches, mism);
        Interlocked.Add(ref _received, payload.Length);
    }

    /// <summary>Fire the two out-of-band sends (called from the main thread once connected).</summary>
    public void FireSends()
    {
        var conn = _serverConn!;
        conn.Send(new ReadOnlySpan<byte>(_pattern));                 // contiguous → multi-page writev
        conn.Send(BuildSequence(_pattern, segSize));                // multi-segment → flatten + writev
    }

    private static ReadOnlySequence<byte> BuildSequence(byte[] data, int segSize)
    {
        BufferSegment? first = null, last = null;
        for (int off = 0; off < data.Length; off += segSize)
        {
            int len = Math.Min(segSize, data.Length - off);
            var mem = new ReadOnlyMemory<byte>(data, off, len);
            if (first is null) first = last = new BufferSegment(mem, 0);
            else last = last!.Append(mem);
        }
        return new ReadOnlySequence<byte>(first!, 0, last!, last!.Memory.Length);
    }

    private sealed class BufferSegment : ReadOnlySequenceSegment<byte>
    {
        public BufferSegment(ReadOnlyMemory<byte> mem, long runningIndex)
        {
            Memory = mem;
            RunningIndex = runningIndex;
        }

        public BufferSegment Append(ReadOnlyMemory<byte> mem)
        {
            var seg = new BufferSegment(mem, RunningIndex + Memory.Length);
            Next = seg;
            return seg;
        }
    }
}
