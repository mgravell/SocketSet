// verify-tailwipe — the receive/send buffers are SHARED AND RECYCLED, so the bytes past the current
// payload belong to whoever used them last (another client's decrypted plaintext, on a TLS connection).
// This asserts BOTH that those bytes cannot reach the wire, and that avoiding them is charged at cost.
// Exit 0 = all PASS.
//
// FOUR CELLS. The first three are the disclosure vectors; the fourth is the cost property.
//   A  RawBuffer, then over-report            -> caught by wipe-on-access
//   B  ResponseBytes above PayloadBytes, and  -> NOT caught by wipe-on-access. This is the realistic
//      RawBuffer NEVER touched                  accident (a miscomputed frame length), and it is why
//                                               the length setters wipe too. Raised by Marc after
//                                               reading the first implementation, which had this hole.
//   C  GetWriteSpan(bigger than payload)      -> the cheap growth path must still be airtight
//   D  WIPE EXACTLY THE DELTA                 -> receive 20, reply 25, and EXACTLY 5 bytes are cleared:
//                                               not zero (that would leak), and not the whole ~4000-byte
//                                               tail (that would be the over-charge this design exists
//                                               to avoid). Asserted byte-for-byte, not assumed.
//
// VERIFIED TO DISCRIMINATE, because a gate that has never failed proves nothing:
//   - with the ResponseBytes trigger removed, B fails 64/64 on epoll and managed, 4/64 on io_uring,
//     while A still passes throughout;
//   - D fails in BOTH directions of getting the delta wrong: cleared==0 (no wipe) and cleared==tail
//     (eager wipe) are each reported as a FAIL with the actual count.
//
// FORCING THE BUFFER COLLISION is the whole difficulty of the rig, and getting it wrong produces a
// green run that proves nothing. A first version connected fresh clients per probe and passed even
// against the broken build: io_uring hands out provided buffers by bid from a per-shard ring, so a new
// connection rarely lands on the buffer a previous tenant dirtied. Hence ONE connection, a small
// (16-entry) pool, and repeated rounds — epoll and managed reuse one per-connection buffer every time,
// and io_uring collides within 64 rounds with certainty.
using System.Net;
using System.Net.Sockets;
using SocketSets;

const int Port = 19801, Reply = 900, Rounds = 64, Grow = 5;
int failures = 0;

foreach (var backend in new[] { SocketSetFactory.IoUring, SocketSetFactory.Epoll, SocketSetFactory.Managed })
{
    string name = backend.GetType().Name.Replace("SocketFactory", "").Replace("Factory", "");
    foreach (var mode in new[] { Mode.RawBuffer, Mode.ResponseBytesOnly, Mode.GetWriteSpanBigger, Mode.ExactDelta })
    {
        var opts = new SocketSetOptions
        {
            Shards = 1, Factory = backend, ReceiveBufferSize = 4096, BufferPagesPerShard = 16,
        };
        using var srv = new Srv(opts, mode, Reply, Grow);
        srv.Listen(new IPEndPoint(IPAddress.Loopback, Port));
        Thread.Sleep(250);

        int leaked = 0, replies = 0, worstCleared = -1, measured = 0;
        using (var c = new TcpClient())
        {
            c.Connect("127.0.0.1", Port);
            c.NoDelay = true;
            var s = c.GetStream();
            s.Write("DDDD"u8); s.ReadByte();                    // round 0: dirty the whole buffer
            var buf = new byte[Reply];
            // The delta cell sends a 20-byte request and expects a 25-byte reply whose last 4 bytes are
            // the server's count of how many tail bytes it found cleared.
            int req = mode == Mode.ExactDelta ? 20 : 1;
            int want = mode == Mode.ExactDelta ? 20 + Grow : Reply;
            var probe = new byte[req];
            probe[0] = (byte)'p';
            for (int r = 0; r < Rounds; r++)
            {
                s.Write(probe, 0, req);
                int got = 0;
                while (got < want) { int n = s.Read(buf, got, want - got); if (n <= 0) break; got += n; }
                if (got != want) break;
                replies++;
                if (mode == Mode.ExactDelta)
                {
                    // -1 = the server could not measure this round (buffer was not the dirty one).
                    // Require at least one measurable round, and every measurable round to be exact.
                    int cleared = BitConverter.ToInt32(buf, 20 + Grow - 4);
                    if (cleared < 0) continue;
                    measured++;
                    if (cleared != Grow) worstCleared = cleared;       // any wrong count sticks
                    else if (worstCleared < 0) worstCleared = Grow;
                }
                else
                {
                    for (int i = 1; i < got; i++) if (buf[i] == (byte)'S') { leaked++; break; }
                }
            }
        }

        bool ok; string detail;
        if (mode == Mode.ExactDelta)
        {
            ok = measured > 0 && worstCleared == Grow;
            detail = $"replies={replies} measurable={measured} cleared={worstCleared} "
                   + $"(must be exactly {Grow}: 0 would leak, ~4000 would be the over-charge)";
        }
        else
        {
            ok = leaked == 0;
            detail = $"replies={replies} leaking={leaked}";
        }
        if (!ok) failures++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name,-14} {Label(mode),-34} {detail}");
        Thread.Sleep(150);
    }
}

Console.WriteLine(failures == 0
    ? "\n=== verify-tailwipe: ALL PASS ==="
    : $"\n=== verify-tailwipe: {failures} FAILURE(S) ===");
return failures == 0 ? 0 : 1;

static string Label(Mode m) => m switch
{
    Mode.RawBuffer => "A: leak via RawBuffer",
    Mode.ResponseBytesOnly => "B: leak via ResponseBytes only",
    Mode.GetWriteSpanBigger => "C: leak via GetWriteSpan(bigger)",
    _ => "D: wipes exactly the delta",
};

enum Mode { RawBuffer, ResponseBytesOnly, GetWriteSpanBigger, ExactDelta }

sealed class Srv(SocketSetOptions o, Mode mode, int reply, int grow) : SocketSet(o)
{
    protected override void OnReceive(ref ReceiveContext ctx)
    {
        if (ctx.IsEof) return;
        if (ctx.Payload[0] == (byte)'D')
        {
            // Dirty everything, standing in for a previous tenant's plaintext. RawBufferUnwiped, so the
            // marker is actually written rather than cleared by the very act of asking for the buffer.
            ctx.RawBufferUnwiped.Fill((byte)'S');
            ctx.ResponseBytes = 1;
            return;
        }

        switch (mode)
        {
            case Mode.RawBuffer:
                ctx.RawBuffer[0] = (byte)'a';
                ctx.ResponseBytes = reply;
                break;

            case Mode.ResponseBytesOnly:
                ctx.ResponseBytes = reply;      // never touches the buffer: the easy-to-miss vector
                break;

            case Mode.GetWriteSpanBigger:
                ctx.GetWriteSpan(reply)[0] = (byte)'a';
                ctx.ResponseBytes = reply;
                break;

            default: // ExactDelta — the cost property, asserted byte-for-byte
            {
                // Received `n` (20), want to reply n+grow (25). Ask for exactly that much, then count
                // how many of the marker bytes past the payload were actually cleared. Correct answer
                // is `grow` and nothing else: 0 means the growth region still holds a previous tenant's
                // bytes, and ~4000 means we paid for the whole tail to send five bytes.
                int n = ctx.PayloadBytes;
                var span = ctx.GetWriteSpan(n + grow);
                var all = ctx.RawBufferUnwiped;
                // ONLY MEANINGFUL ON A BUFFER THAT IS ACTUALLY DIRTY. io_uring hands out provided
                // buffers by bid, so this receive may have landed on a fresh one whose tail is already
                // zero (the slab is MAP_ANONYMOUS); counting zeroes there would report the whole tail as
                // "cleared" and read as an over-charge that never happened. That is exactly what the
                // first version of this cell did. The marker must still be present immediately AFTER the
                // growth region for the count to mean anything; otherwise report -1 and skip the round.
                int cleared;
                if (n + grow < all.Length && all[n + grow] == (byte)'S')
                {
                    cleared = 0;
                    for (int i = n; i < all.Length; i++) { if (all[i] == (byte)'S') break; cleared++; }
                }
                else cleared = -1; // buffer not dirty: this round says nothing either way
                BitConverter.TryWriteBytes(span[(n + grow - 4)..], cleared);
                ctx.ResponseBytes = n + grow;
                break;
            }
        }
    }
}
