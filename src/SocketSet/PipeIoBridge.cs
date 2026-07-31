#if NET // System.IO.Pipelines is not referenced by the netfx build; pipe mode is net-only.
using System.Buffers;      // BuffersExtensions.Write, for PipeWriter as an IBufferWriter<byte>
using System.IO.Pipelines;

namespace SocketSets;

/// <summary>
/// Drives a connection's I/O through a caller-supplied <see cref="IDuplexPipe"/> instead of the
/// <c>OnReceive</c>/<c>OnWrite</c> callbacks. Opt in per connection from OnAccept/OnConnect with
/// <c>ctx.UsePipe(pipe)</c>; a connection that never calls it is completely unaffected, so this cannot
/// regress the existing path.
///
/// ORIENTATION. The pipe handed in is the TRANSPORT-side endpoint, matching Kestrel's
/// <c>IConnectionContext.Application</c>:
///   inbound  (socket -> app) : the transport WRITES to <c>pipe.Output</c>
///   outbound (app -> socket) : the transport READS from <c>pipe.Input</c>
/// The caller reads what it received from the other end of the same pair, and writes what it wants sent.
///
/// THIS IS THE FALLBACK PATH, deliberately. It is written entirely against the existing public surface -
/// <see cref="Connection.Send(in System.Buffers.ReadOnlySequence{byte})"/> outbound and the normal receive
/// callback inbound - so it works on EVERY backend today, including ones that can never do better (RIO
/// takes only registered buffer ids, never foreign addresses; a DPDK-style backend would be similar). It
/// costs one copy in each direction, which is exactly what a zero-copy per-backend implementation is
/// supposed to remove later. Getting the semantics right here first means each backend's fast path has a
/// reference to be measured against rather than being designed blind.
///
/// KNOWN LIMITATIONS of this fallback, all of which the per-backend paths are expected to fix:
///  - Inbound backpressure is advisory. The receive callback runs on the loop thread and cannot block, so
///    a flush that does not complete synchronously is observed asynchronously and writing continues into
///    the PipeWriter's own buffer. Honouring it properly means PARKING the receive, which needs backend
///    cooperation (do not re-arm until the flush completes).
///  - <paramref name="pinned"/> is recorded and not yet acted on. It exists so a caller whose pipe is
///    backed by pinned memory (Kestrel's PinnedBlockMemoryPool, or a pool over the pinned object heap)
///    can tell the backend that per-operation pinning is unnecessary. Nothing in this fallback pins
///    anything, because it copies.
///  - Read depth (multiple receives in flight) and the instant-response RawBuffer path are incompatible
///    with pipe mode by construction: both hand out transport-owned memory whose lifetime does not match
///    a pipe segment's.
///
/// While a connection is in pipe mode the application must not also use
/// <see cref="Connection.Send(System.ReadOnlySpan{byte})"/> or the IBufferWriter surface directly: the
/// outbound pump owns ordering on that half, and an interleaved write would land between pipe segments.
/// </summary>
internal sealed class PipeIoBridge
{
    private readonly Connection _conn;
    private readonly IDuplexPipe _pipe;

    /// <summary>Caller's assertion that the pipe's memory is already pinned. Advisory today; consumed by
    /// per-backend fast paths that would otherwise pin each buffer for the duration of an operation.</summary>
    internal readonly bool Pinned;

    private int _completed; // 0 = live, 1 = teardown already run (Interlocked; both threads can reach it)

    private PipeIoBridge(Connection conn, IDuplexPipe pipe, bool pinned)
    {
        _conn = conn;
        _pipe = pipe;
        Pinned = pinned;
    }

    /// <summary>Attach a pipe to a connection and start pumping its outbound half. Called from
    /// OnAccept/OnConnect, i.e. on the owning loop thread.</summary>
    internal static PipeIoBridge Attach(Connection conn, IDuplexPipe pipe, bool pinned)
    {
        var bridge = new PipeIoBridge(conn, pipe, pinned);
        conn.PipeIo = bridge;
        // The outbound half is a straightforward async loop, so it runs off the loop thread: awaiting
        // ReadAsync is what applies backpressure to the application, and the loop thread must never wait.
        _ = Task.Run(bridge.PumpOutboundAsync);
        return bridge;
    }

    // Inbound serialization. A PipeWriter permits ONE outstanding FlushAsync: writing or flushing again
    // while one is in flight throws "Concurrent reads or writes are not supported" and, from the loop
    // thread, faults the whole shard. That is not hypothetical - it is what a 64KB chunk size did on
    // 2026-07-28, once a test finally exercised pipe mode deeply enough to make a flush go async.
    //
    // The loop thread cannot wait for a flush, so bytes that arrive during one are STAGED here and written
    // when it completes. That costs a copy, but only under backpressure, and only until a backend can park
    // its receive instead (the proper fix, which needs per-backend cooperation).
    private readonly object _inGate = new();
    private bool _flushPending;
    private readonly Queue<ArraySegment<byte>> _staged = new();

    // ---- SS_BRIDGE_STATS=1: how expensive IS the inbound path, really? --------------------------------
    //
    // Added 2026-07-31, BEFORE building receive parking, to decide whether it is worth building at all.
    // Parking's whole payoff is removing the SECOND copy - the staged one - and making backpressure real.
    // If flushes almost always complete synchronously under real load then staging is rare, parking buys
    // nothing measurable, and the honest thing is to say so rather than ship a cross-backend change on a
    // plausible story. This is the inbound version of "confirm the fast path was TAKEN": measure how
    // often the slow path is even reached before optimising it.
    //
    // Off by default (a static readonly bool read once), like the three backend counters.
    private static readonly bool ReportStats =
        Environment.GetEnvironmentVariable("SS_BRIDGE_STATS") == "1";

    // A/B control: SS_NO_ZC_RECV=1 forces the copying receive path even where zero-copy receive is
    // possible, to measure what reading straight into pipe memory is actually worth. Read once.
    private static readonly bool NoZeroCopyRecv =
        Environment.GetEnvironmentVariable("SS_NO_ZC_RECV") == "1";

    private static long s_recv, s_recvBytes;        // inbound callbacks and their bytes (copy #1, always paid)
    private static long s_flushSync, s_flushAsync;  // flushes that completed inline vs went async
    private static long s_staged, s_stagedBytes;    // copy #2: only paid when a flush was outstanding

    private static void DumpStats(string tag)
    {
        long recv = Interlocked.Read(ref s_recv);
        if (recv == 0) return;
        long staged = Interlocked.Read(ref s_staged);
        long async_ = Interlocked.Read(ref s_flushAsync);
        long zc = Interlocked.Read(ref s_recvZeroCopy);
        Console.Error.WriteLine($"[bridge-stats:{tag}] receives={recv:n0} ({Interlocked.Read(ref s_recvBytes) / (double)(1 << 20):n1} MiB) " +
            $"zero-copy-recv={zc:n0} ({(recv > 0 ? 100.0 * zc / recv : 0):n1}% of receives) | " +
            $"flush: sync={Interlocked.Read(ref s_flushSync):n0} async={async_:n0} ({(recv > 0 ? 100.0 * async_ / recv : 0):n1}% of receives) | " +
            $"STAGED (second copy)={staged:n0} ({(recv > 0 ? 100.0 * staged / recv : 0):n1}% of receives, " +
            $"{Interlocked.Read(ref s_stagedBytes) / (double)(1 << 20):n1} MiB)");
    }

    private static readonly Timer? StatsTimer = ReportStats
        ? new Timer(static _ => DumpStats("periodic"), null, 2000, 2000)
        : null;

    private static long s_recvZeroCopy; // receives read straight into pipe memory (no transport-side copy)

    /// <summary>
    /// ZERO-COPY RECEIVE, for a readiness backend that reads on demand (epoll). Hand out a slice of the
    /// pipe's OWN output memory to <c>recv()</c> into, so inbound bytes land in the pipe with NO
    /// transport-side copy — paired 1:1 with <see cref="CommitReceive"/>. Returns false when the writer
    /// cannot be touched right now (a flush is outstanding, or the bridge is torn down); the caller then
    /// falls back to its copying path (<see cref="OnReceived"/>).
    ///
    /// WHY ONLY A READINESS BACKEND. A completion backend (io_uring) must arm a receive with a buffer
    /// BEFORE the data arrives, so it would have to reserve pipe memory speculatively; epoll asks for the
    /// buffer only once EPOLLIN says data is waiting, so it can read straight into the pipe with no
    /// inversion. See TODO item 7. Loop-thread only (the same thread that calls OnReceived).
    /// </summary>
    internal bool TryBeginReceive(int sizeHint, out Memory<byte> buffer)
    {
        buffer = default;
        if (NoZeroCopyRecv) return false; // A/B: force the copy path
        lock (_inGate)
        {
            // A flush in flight means the writer is off-limits (concurrent-write throw); fall back to the
            // copy+stage path, which is the ~0.1%-of-receives case (SS_BRIDGE_STATS).
            if (Volatile.Read(ref _completed) != 0 || _flushPending) return false;
            buffer = _pipe.Output.GetMemory(sizeHint);
            return true;
        }
    }

    /// <summary>Commit <paramref name="bytes"/> that were <c>recv()</c>'d into the buffer from
    /// <see cref="TryBeginReceive"/>: advance the writer and flush. If the flush goes async, subsequent
    /// receives stage behind it exactly as <see cref="OnReceived"/> does, until it drains.</summary>
    internal void CommitReceive(int bytes)
    {
        if (bytes <= 0) return;
        if (ReportStats) { Interlocked.Increment(ref s_recv); Interlocked.Add(ref s_recvBytes, bytes); Interlocked.Increment(ref s_recvZeroCopy); }

        ValueTask<FlushResult> flush;
        lock (_inGate)
        {
            _pipe.Output.Advance(bytes);
            flush = _pipe.Output.FlushAsync();
            if (flush.IsCompletedSuccessfully)
            {
                if (ReportStats) Interlocked.Increment(ref s_flushSync);
                var r = flush.Result;
                if (r.IsCompleted || r.IsCanceled) _conn.Close();
                return;
            }
            if (ReportStats) Interlocked.Increment(ref s_flushAsync);
            _flushPending = true;
        }
        _ = DrainFlushesAsync(flush);
    }

    /// <summary>Inbound data, on the loop thread, in place of <c>OnReceive</c>.</summary>
    internal void OnReceived(ReadOnlySpan<byte> data)
    {
        if (Volatile.Read(ref _completed) != 0 || data.IsEmpty) return;

        if (ReportStats) { Interlocked.Increment(ref s_recv); Interlocked.Add(ref s_recvBytes, data.Length); }

        ValueTask<FlushResult> flush;
        lock (_inGate)
        {
            if (_flushPending)
            {
                // A flush is outstanding: touching the writer now would throw. Stage instead.
                var buf = ArrayPool<byte>.Shared.Rent(data.Length);
                data.CopyTo(buf);
                _staged.Enqueue(new ArraySegment<byte>(buf, 0, data.Length));
                if (ReportStats) { Interlocked.Increment(ref s_staged); Interlocked.Add(ref s_stagedBytes, data.Length); }
                return;
            }

            _pipe.Output.Write(data);
            flush = _pipe.Output.FlushAsync();
            if (flush.IsCompletedSuccessfully)
            {
                if (ReportStats) Interlocked.Increment(ref s_flushSync);
                var r = flush.Result;
                if (r.IsCompleted || r.IsCanceled) _conn.Close(); // reader is gone
                return;
            }
            if (ReportStats) Interlocked.Increment(ref s_flushAsync);
            _flushPending = true;
        }
        _ = DrainFlushesAsync(flush);
    }

    /// <summary>Await an outstanding flush, then write anything staged behind it and flush again, until
    /// nothing is left. Runs off the loop thread; the lock only ever guards short bookkeeping.</summary>
    private async Task DrainFlushesAsync(ValueTask<FlushResult> flush)
    {
        while (true)
        {
            try
            {
                var r = await flush.ConfigureAwait(false);
                if (r.IsCompleted || r.IsCanceled) { Fail(); return; }
            }
            catch { Fail(); return; }

            lock (_inGate)
            {
                if (_staged.Count == 0) { _flushPending = false; return; } // caught up; loop may write again
                while (_staged.Count > 0)
                {
                    var seg = _staged.Dequeue();
                    _pipe.Output.Write(seg.AsSpan());
                    ArrayPool<byte>.Shared.Return(seg.Array!);
                }
                flush = _pipe.Output.FlushAsync();
                if (flush.IsCompletedSuccessfully)
                {
                    var r2 = flush.Result;
                    if (r2.IsCompleted || r2.IsCanceled) { Fail(); return; }
                    if (_staged.Count == 0) { _flushPending = false; return; }
                    continue; // more arrived while we were writing; go round without awaiting
                }
            }
        }

        void Fail()
        {
            lock (_inGate)
            {
                _flushPending = false;
                while (_staged.Count > 0) ArrayPool<byte>.Shared.Return(_staged.Dequeue().Array!);
            }
            _conn.Close();
        }
    }

    private async Task PumpOutboundAsync()
    {
        var input = _pipe.Input;
        Exception? fault = null;
        try
        {
            while (true)
            {
                var result = await input.ReadAsync().ConfigureAwait(false);
                var buffer = result.Buffer;

                // How much of `buffer` we may hand back to the pipe. Not always buffer.End: a backend may
                // accept only a PREFIX (see Connection.TrySendZeroCopy), in which case the remainder stays
                // unconsumed and the next ReadAsync returns it immediately.
                var consumed = buffer.Start;
                bool drained = buffer.IsEmpty;

                if (!buffer.IsEmpty)
                {
                    // Prefer zero-copy: the backend sends straight out of the pipe's own segments. It
                    // returns 0 when it cannot at all (RIO, managed, TLS), and then we take the copying
                    // path, which is always correct.
                    //
                    // Note the ordering rule this creates: with zero-copy the socket is reading the pipe's
                    // memory, so AdvanceTo must wait for the send to COMPLETE, not merely to be submitted.
                    // That is the whole reason this awaits before advancing.
                    long accepted = _conn.TrySendZeroCopy(in buffer, Pinned, out var sent);
                    if (accepted > 0)
                    {
                        if (!await sent.ConfigureAwait(false))
                        {
                            input.AdvanceTo(buffer.Start); // connection gone; do not consume what we could not send
                            break;
                        }
                        consumed = buffer.GetPosition(accepted);
                        drained = accepted == buffer.Length;
                    }
                    else if (!_conn.Send(in buffer))
                    {
                        input.AdvanceTo(buffer.Start); // connection gone; do not consume what we cannot send
                        break;
                    }
                    else { consumed = buffer.End; drained = true; }
                }

                input.AdvanceTo(consumed);
                // Only stop on completion once everything really has gone out: with a partial accept there
                // is still a remainder, and IsCompleted stays set for the next read that returns it.
                if ((result.IsCompleted || result.IsCanceled) && drained) break;
            }
        }
        catch (Exception ex)
        {
            fault = ex;
        }
        finally
        {
            try { await input.CompleteAsync(fault).ConfigureAwait(false); } catch { }
            // The application finished writing (or faulted), so the connection has nothing left to send.
            Teardown(fault);
            _conn.Close();
        }
    }

    /// <summary>The connection has gone away; unblock whoever is waiting on either half.</summary>
    internal void OnConnectionClosed() => Teardown(null);

    private void Teardown(Exception? fault)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0) return;
        lock (_inGate)
        {
            while (_staged.Count > 0) ArrayPool<byte>.Shared.Return(_staged.Dequeue().Array!);
        }
        try { _pipe.Output.Complete(fault); } catch { }
        try { _pipe.Input.Complete(fault); } catch { }
    }
}
#endif
