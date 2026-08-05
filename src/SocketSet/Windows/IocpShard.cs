#if NET // Windows IOCP backend; compiled out of the netfx fallback build.
using System.Buffers;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using SocketSets.Native;
using SocketSets.Tls;

namespace SocketSets.Windows;

/// <summary>
/// A single-threaded IOCP event loop — the Windows analogue of <c>IoUringShard</c>. Exactly one
/// thread owns the completion port (created with concurrency 1); cross-thread work (accept hand-off,
/// Close) is marshaled in and the loop woken with <see cref="Win32.PostQueuedCompletionStatus"/> (the
/// eventfd analogue), and completions are drained in batches with
/// <see cref="Win32.GetQueuedCompletionStatusExBlocking"/>. <see cref="Listen"/>/<see cref="Connect"/> submit
/// their overlapped ops directly (Winsock allows this from any thread); only per-connection state
/// mutation and completion processing are loop-thread-exclusive.
///
/// Data path (this slice — first light; not yet exercised on Windows):
///  - accept: one outstanding <c>AcceptEx</c> per listener, re-posted on completion; the accepted
///    socket is bounced round-robin to a shard which associates + arms it (IOCP has no reuse-port
///    load balancing, so there is a single acceptor — TODO: post N accepts / accept from N threads).
///  - connect: <c>ConnectEx</c> (requires a bound socket).
///  - recv: one <c>WSARecv</c> per connection, continuously re-armed; a per-connection recv buffer.
///  - send: copy-based echo through the write-buffer pool, one send in flight per connection
///    (SendBusy), an echo that arrives mid-send is copied out and queued (no no-copy path yet).
///  - close: <c>closesocket</c> aborts the pending recv/send; the slot is held (defer-recycle) until
///    those completions drain, so no stale completion lands on a re-tenanted slot.
/// </summary>
internal sealed unsafe class IocpShard : WindowsShardBase<IocpConnection>
{
    private const int EntryBatch = 128;              // completions dequeued per GetQueuedCompletionStatusEx
    private const int AddrStride = 128;              // per-address storage for AcceptEx (covers sockaddr_in and _un)
    private const int AcceptBufSize = 2 * AddrStride; // AcceptEx output buffer: local + remote sockaddr, no initial data
    private static readonly nuint WakeKey = unchecked((nuint)(-1)); // reserved completion key for PQCS wakes

    // Per-operation context: an OVERLAPPED (which the kernel writes and hands back) plus our own state.
    // The OVERLAPPED MUST be the first field so an OVERLAPPED* is bit-identical to the op ctx* — we cast
    // straight back on completion (no CONTAINING_RECORD offset). Blittable, so it lives in native memory.
    internal enum OpKind : int { Accept = 0, Connect = 1, Recv = 2, Send = 3 }

    // Recv/Send/Connect op context. Kind sits right after the OVERLAPPED, at the same offset as in
    // AcceptOp, so the loop can read Kind through an IocpOp* regardless of the real op type.
    [StructLayout(LayoutKind.Sequential)]
    internal struct IocpOp
    {
        public Win32.OVERLAPPED Overlapped; // MUST be first (offset 0)
        public OpKind Kind;
        public uint Slot;
        public int Buf;                     // recv-pool index (Recv) / write-pool index (Send)

        // The connection generation this op was armed for. A completion carries only a SLOT, so nothing
        // in the completion itself distinguishes "my connection" from "whoever holds this slot now" -
        // the defer-recycle rule (TryFinalize refusing to free while RecvArmed || SendBusy) is the ONLY
        // thing preventing a stale completion landing on a re-tenanted slot. That rule should hold, so
        // this check should never fire; it exists because item 0e cost a day to find and the next
        // lifetime bug should announce itself instead of corrupting a live connection's state.
        public uint Generation;
    }

    // Accept op context. No slot yet (there is no connection until the accept completes), so it carries
    // a GCHandle to its managed AcceptState instead. Same {OVERLAPPED, Kind} prefix as IocpOp.
    [StructLayout(LayoutKind.Sequential)]
    internal struct AcceptOp
    {
        public Win32.OVERLAPPED Overlapped; // MUST be first (offset 0)
        public OpKind Kind;
        public nint Handle;                 // GCHandle.ToIntPtr(AcceptState)
    }

    // Everything a single outstanding AcceptEx needs. Reused across accepts on one listener.
    private sealed class AcceptState
    {
        public nint Listener;
        public nint AcceptSocket;
        public nint Buf;      // (byte*) native AcceptEx output buffer (AcceptBufSize)
        public nint Op;       // (AcceptOp*) native op context
        public object? Token; // default UserToken for connections accepted here
        public int Af;        // family/proto for creating the next accept socket
        public int Proto;
        public GCHandle Gc;   // keeps this instance alive + gives a stable identity in AcceptOp.Handle
    }

    // --- slot table (1-based ids; id 0 == "none"). Connections are pooled and reused. ---
                                  // it's a mutable struct, so a readonly field would mutate a throwaway copy.
    // Connect requests marshaled from the caller thread to the loop, which claims the slot + posts
    // ConnectEx — so the slot table stays single-writer. The socket is created caller-side (thread-agnostic
    // syscalls, sync failures stay synchronous); the port-assoc + ConnectEx run on the loop (their failures
    // become async, uniform with accept).
    private readonly ConcurrentQueue<(nint Socket, EndPoint Endpoint, object? Token)> _pendingConnects = [];

    // --- options snapshot ---
    private readonly int _opCount;

    // --- created on the loop thread in OnInitialize() ---
    private Win32.OVERLAPPED_ENTRY* _entries;        // GQCSEx batch buffer
    private IocpOp* _ops;                            // op-context slab: recv=[2i], send=[2i+1] per slot i
    private byte* _connectAddrs;                     // per-slot stable sockaddr storage for ConnectEx
    private volatile bool _portReady;

    // TLS scratch, shared by every connection on this shard (null unless Options.Tls is set). Safe to share
    // because a shard has ONE loop thread and a filter is only ever touched from it — the managed backend
    // needs a per-connection gate precisely because it has no such thread.

    // Accept states (one per listener). Only mutated under _acceptGate; iterated at shutdown (loop stopped).
    private readonly List<AcceptState> _acceptStates = [];
    private readonly object _acceptGate = new();

    // --- cross-thread queues drained on the loop thread ---
    // Accepted sockets handed to this shard by the single acceptor. The default token travels with the
    // socket since the target shard has no listener of its own to look it up on.
    private readonly ConcurrentQueue<(nint Socket, object? Token)> _incoming = [];
    // Close requests marshaled from arbitrary threads. Generation-guarded so a request can't retract a
    // slot that has since been closed and re-tenanted.
    private readonly ConcurrentQueue<(uint Slot, uint Generation)> _closes = [];
    // Out-of-band flushed writes (Connection.Flush from any thread): a private byte[] + length + the
    // capturing generation, sent on the loop through the normal Pending path. Generation-guarded.
    private readonly ConcurrentQueue<(uint Slot, uint Generation, byte[] Data, int Len)> _flush = [];
    // Parked receives to re-arm (Connection.ResumeReceive, from the consumer's flush continuation).
    // Generation-guarded for the same reason as the two above: the slot may have been re-tenanted.
    private readonly ConcurrentQueue<(uint Slot, uint Generation)> _resumes = [];

    // Synchronous (FILE_SKIP_COMPLETION_PORT_ON_SUCCESS) recv/send completions that posted no port
    // packet. Loop-thread-only. Deferred here and drained ITERATIVELY (not by calling the handler
    // recursively): a saturated connection completes recv→echo→recv→… synchronously and would otherwise
    // recurse straight into a stack overflow. Bounded per pass (InlineBurst) so one busy or flooding
    // connection can't starve the port, other connections, or the IsActive/shutdown check.
    private struct InlineOp { public OpKind Kind; public uint Slot; public uint Bytes; public bool Failed; }
    private const int InlineBurst = 512;
    private readonly Queue<InlineOp> _inline = new();

    public IocpShard(SocketSetOptions options) : base(options)
    {
        _opCount = _socketsPerShard * 2;       // recv + send per connection
        // Everything native is deferred to OnInitialize (loop thread); the ctor stays inert so the
        // factory can be constructed on any OS.

        // Pre-allocate the connection table: one pooled instance per slot, reused across connection
        // lifetimes so accept/connect never allocates. The slot count is a hard cap on concurrent
        // connections per shard (InitClient returns null when full). A Connection is therefore a lease
        // on a slot, not an ownable object — a reference held past OnClosed may by then be a different
        // logical connection that reused the slot. Reuse is safe because every use is gated by the
        // per-slot Generation token (bumped on each InitClient): Close/writes capture the generation and
        // are dropped, not misdelivered, if the slot has since been re-tenanted — the same pattern as
        // IValueTaskSource's token, which validates a stashed ValueTask against the source's current
        // version so a stale await can't observe a pooled source's next result.
        _conns = new IocpConnection[_socketsPerShard];
        for (int i = 0; i < _conns.Length; i++)
            _conns[i] = new IocpConnection(this, (uint)i + 1);
        _slots = new SlotAllocator(_conns.Length);
        SetShardCapacity(_conns.Length); // reservation ceiling == slot-table size
    }

    // WSAStartup once per process; WSACleanup is left to process exit.
    private static readonly object _wsaGate = new();
    private static bool _wsaStarted;

    private static void EnsureWinsock()
    {
        // Double-checked: the flag is published only AFTER WSAStartup returns success, so (a) a failed
        // startup stays retryable rather than poisoning the flag, and (b) concurrent shard inits block
        // on the gate until the winner has truly finished — a fast return here must mean Winsock is up,
        // not merely "being brought up" (otherwise a racing caller hits WSANOTINITIALISED).
        if (Volatile.Read(ref _wsaStarted)) return;
        lock (_wsaGate)
        {
            if (_wsaStarted) return;
            byte* wsaData = stackalloc byte[512]; // WSADATA — we never read it
            int rc = Win32.WSAStartup(0x0202, wsaData); // request Winsock 2.2
            if (rc != 0) throw new InvalidOperationException($"WSAStartup failed: {rc}");
            Volatile.Write(ref _wsaStarted, true);
        }
    }

    /// <summary>IOCP's drain consumes a Pending segment across as many write pages as it needs
    /// (see <see cref="DrainPendingIntoPages"/> and <see cref="WindowsConnection.PendingHeadOffset"/>), so
    /// ciphertext can be staged whole instead of pre-chunked and copied. RIO's drain does not, and is
    /// deliberately left on the copying path until it does.</summary>
    protected override bool SupportsOwnedStaging => true;

    protected override void OnInitialize()
    {
        EnsureWinsock();

        // Fresh port, concurrency 1 (a single dedicated thread services it). NULL/0 on failure.
        _port = Win32.CreateIoCompletionPort(Win32.INVALID_HANDLE_VALUE, 0, 0, 1);
        if (_port == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateIoCompletionPort failed");

        _writeBuffer = new PinnedWriteBufferPool(_writeCount, _writeBufSize);
        _recvBuffer = new PinnedWriteBufferPool(_recvCount, _recvBufSize);
        _entries = (Win32.OVERLAPPED_ENTRY*)NativeMemory.AllocZeroed(EntryBatch * (nuint)sizeof(Win32.OVERLAPPED_ENTRY));
        // Op-context OVERLAPPEDs are zeroed once here and never re-zeroed per op: we never set hEvent (it
        // stays null → IOCP notification), Offset/OffsetHigh are ignored for socket I/O, and the kernel
        // overwrites Internal/InternalHigh on every completion. So the submit paths set only Kind/Slot/Buf.
        _ops = (IocpOp*)NativeMemory.AllocZeroed((nuint)_opCount * (nuint)sizeof(IocpOp));
        _connectAddrs = (byte*)NativeMemory.AllocZeroed((nuint)_socketsPerShard * AddrStride);
        if (Parent.Options.Tls is not null)
        {
            _tlsPlain = new PooledBufferWriter(_recvBufSize);
            _tlsCipher = new PooledBufferWriter(_writeBufSize);
            _tlsCtrl = new PooledBufferWriter(1024);
        }
        _portReady = true;
    }

    protected override void OnRun()
    {
        PinLoopThread();

        while (IsActive)
        {
            // At the TOP, not after the completion batch: the loop has early `continue`s (WAIT_TIMEOUT,
            // port closed) that would skip a sweep placed below, and the sweep timer's Poke is what
            // brings us back round here in the first place.
            MaybeSweep();

            // Honour marshaled cross-thread work before blocking for completions (a wake packet is what
            // unblocks GQCSEx when new work is enqueued).
            DrainCrossThread();

            // Process synchronous (FILE_SKIP) completions before we consider blocking — a recv/send that
            // completed inline posts no packet, so leaving one queued while we block would strand it.
            DrainInline();

            // Block only when there's no pending inline work; otherwise poll (timeout 0, GC-transition
            // suppressed) so inline bursts interleave with the port and IsActive stays responsive.
            uint removed = 0;
            bool ok = _inline.Count > 0
                ? Win32.GetQueuedCompletionStatusExNonBlocking(_port, _entries, EntryBatch, &removed, 0, alertable: false)
                : Win32.GetQueuedCompletionStatusExBlocking(_port, _entries, EntryBatch, &removed, Win32.INFINITE, alertable: false);
            if (!ok)
            {
                // WAIT_TIMEOUT (nothing ready on a poll), or the port closed during shutdown
                // (ERROR_ABANDONED_WAIT_0) — the IsActive check ends the loop. Re-loop either way.
                continue;
            }

            for (uint i = 0; i < removed; i++)
            {
                ref Win32.OVERLAPPED_ENTRY e = ref _entries[i];
                if (e.lpCompletionKey == WakeKey || e.lpOverlapped == null)
                    continue; // wake packet: work is drained at the top of the next iteration

                // Real I/O completion: OVERLAPPED is the first field, so the OVERLAPPED* IS the op ctx.
                // Kind sits at the same offset in every op-ctx type, so read it through IocpOp*.
                IocpOp* op = (IocpOp*)e.lpOverlapped;
                bool failed = e.lpOverlapped->Internal != 0; // NTSTATUS; 0 == STATUS_SUCCESS
                uint bytes = e.dwNumberOfBytesTransferred;
                switch (op->Kind)
                {
                    case OpKind.Accept: HandleAccept((AcceptOp*)e.lpOverlapped, failed); break;
                    case OpKind.Connect: HandleConnect(op->Slot, failed); break;
                    case OpKind.Recv:
                        if (StaleCompletion(op)) break;
                        HandleRecv(op->Slot, bytes, failed); break;
                    case OpKind.Send:
                        if (StaleCompletion(op)) break;
                        HandleSend(op->Slot, bytes, failed); break;
                }
            }
        }
    }

    private void DrainCrossThread()
    {
        DrainAwaitingPage(); // retry anyone who was waiting on a write page before taking on more work
        while (_incoming.TryDequeue(out var inbound))
            AdoptAccepted(inbound.Socket, inbound.Token);

        while (_pendingConnects.TryDequeue(out var pc))
            StartConnect(pc.Socket, pc.Endpoint, pc.Token);

        while (_closes.TryDequeue(out var c))
        {
            var conn = _conns[c.Slot - 1];
            if (conn.Generation == c.Generation && conn.Socket != 0) CloseClient(c.Slot);
        }

        // After the closes, so a close and a resume landing in the same pass resolve as "closed".
        while (_resumes.TryDequeue(out var r)) ResumeRecv(r.Slot, r.Generation);

        // f.Data is rented (see OutboundConnection.Flush) and owned by this loop now: return it however
        // PumpFlush exits, including the drop paths where the slot was re-tenanted.
        while (_flush.TryDequeue(out var f))
        {
            try { PumpFlush(f.Slot, f.Generation, f.Data, f.Len); }
            finally { ArrayPool<byte>.Shared.Return(f.Data); }
        }

        // Zero-copy sends. The pump already pinned the segments, so this only validates the slot and
        // posts. A re-tenanted or closing slot must still unpin and release the pump, or the pump waits
        // forever on a connection that no longer exists.
        while (_zeroCopy.TryDequeue(out var z))
        {
            var conn = _conns[z.Slot - 1];
            if (conn.Generation != z.Generation || conn.Socket == 0 || conn.Closing
                || (conn.Flags & SocketSet.SocketFlags.SendClosed) != 0)
            {
                FinishZeroCopy(conn, ok: false);
                continue;
            }
            if (conn.SendBusy) { conn.ZcPending = true; continue; } // issued from CompleteWrite
            StartZeroCopy(conn, z.Slot);
        }
    }

    // Defer a synchronously-completed recv/send (no port packet was posted for it). Loop-thread-only.
    private void QueueInline(OpKind kind, uint slot, uint bytes, bool failed)
        => _inline.Enqueue(new InlineOp { Kind = kind, Slot = slot, Bytes = bytes, Failed = failed });

    // Drain deferred synchronous completions iteratively (handlers may enqueue more as they re-arm/echo).
    // Bounded per pass so the loop periodically re-checks the port and IsActive.
    private void DrainInline()
    {
        for (int budget = InlineBurst; budget > 0 && _inline.Count > 0; budget--)
        {
            var io = _inline.Dequeue();
            switch (io.Kind)
            {
                case OpKind.Recv: HandleRecv(io.Slot, io.Bytes, io.Failed); break;
                case OpKind.Send: HandleSend(io.Slot, io.Bytes, io.Failed); break;
            }
        }
    }

    protected override void OnStop() => Poke(); // wake the loop so it observes !IsActive

    protected override void Wake() => Poke(); // the sweep timer's doorbell (see SocketSetShard)

    protected override void OnShutdown()
    {
        if (ReportStats) DumpStats("shutdown");
        _portReady = false;

        // Close listeners + outstanding accept sockets and free their native state (loop stopped → no race).
        lock (_acceptGate)
        {
            foreach (var st in _acceptStates)
            {
                if (st.AcceptSocket != 0) Win32.closesocket(st.AcceptSocket);
                if (st.Listener != 0) Win32.closesocket(st.Listener);
                if (st.Buf != 0) NativeMemory.Free((void*)st.Buf);
                if (st.Op != 0) NativeMemory.Free((void*)st.Op);
                if (st.Gc.IsAllocated) st.Gc.Free();
            }
            _acceptStates.Clear();
        }

        // Close any still-live connection sockets.
        for (int i = 0; i < _conns.Length; i++)
        {
            nint s = Interlocked.Exchange(ref _conns[i].Socket, 0);
            if (s != 0) Win32.closesocket(s);
            var tls = _conns[i].Tls;
            if (tls is not null) { _conns[i].Tls = null; tls.Dispose(); }
        }

        _tlsPlain?.Dispose(); _tlsPlain = null;
        _tlsCipher?.Dispose(); _tlsCipher = null;
        _tlsCtrl?.Dispose(); _tlsCtrl = null;

        if (_ops != null) { NativeMemory.Free(_ops); _ops = null; }
        if (_entries != null) { NativeMemory.Free(_entries); _entries = null; }
        if (_connectAddrs != null) { NativeMemory.Free(_connectAddrs); _connectAddrs = null; }
        _writeBuffer.Dispose();
        _recvBuffer.Dispose();
        if (_port != 0) { Win32.CloseHandle(_port); _port = 0; }
    }

    /// <summary>Queue a wake packet (verbatim key, no I/O, null overlapped) so the loop re-checks its
    /// cross-thread queues / IsActive. The eventfd analogue.</summary>
    private void Poke()
    {
        if (!_portReady) return;
        Win32.PostQueuedCompletionStatus(_port, 0, WakeKey, null);
    }

    /// <summary>Marshal an accepted socket onto this shard's loop (called from the acceptor shard's
    /// loop). The socket is process-global so any port can adopt it.</summary>
    internal void EnqueueInbound(nint socket, object? token)
    {
        _incoming.Enqueue((socket, token));
        Poke();
    }

    // Zero-copy send requests marshaled from the outbound pump thread. The SEGMENTS are pinned on the
    // calling thread (Memory.Pin is thread-agnostic) so the loop only has to build WSABUFs and post.
    private readonly ConcurrentQueue<(uint Slot, uint Generation)> _zeroCopy = [];

    // ---- SS_IOCP_STATS=1: did the fast path RUN, or did it decline? ----------------------------------
    //
    // Added 2026-07-29, and the reason is a measurement that could not be interpreted without it. IOCP's
    // zero-copy send measured +3.5% at 16KB and nothing at 256KB, which was read as "the copy was never
    // the cost". io_uring then measured +45.1% at 256KB for the same change, and its zero-copy= segment
    // counter showed why: a 256KB response through Kestrel's default ~4KB pipe blocks is 65.00 segments,
    // against MaxSendPages = 64 here. A path that silently declines measures exactly like one that ran
    // and did not pay - bench/README.md rule 2 - and IOCP had no way to tell those apart.
    //
    // So the decline is counted BY CAUSE, and the fragmentation case additionally records the true
    // segment count (the accept loop early-exits at 65, so the real count needs a second walk - taken
    // only when stats are on, which is why the whole block is gated rather than merely reported).
    private static readonly bool ReportStats =
        Environment.GetEnvironmentVariable("SS_IOCP_STATS") == "1";

    private static long s_zcTaken, s_zcSegs;                       // accepted zero-copy sends, and their segments
    private static long s_zcPrefix;                                // ...of which sent a PREFIX and left a remainder
    private static long s_zcDeclineTls, s_zcDeclineClosed, s_zcDeclineEmpty, s_zcDeclineSegs;
    private static long s_zcDeclineSegSum, s_zcDeclineSegMax;      // true segment count at a fragmentation decline
    private static long s_staleCompletions;                        // MUST stay 0 - see StaleCompletion
    private static long s_sendPages, s_sendPageBufs;               // the COPYING path: WSASends, and their WSABUFs

    /// <summary>
    /// A completion whose op was armed for a PREVIOUS tenant of this slot. Should be impossible: a slot
    /// is not freed while an op is outstanding (TryFinalize refuses while RecvArmed || SendBusy), which
    /// is what makes it safe for a completion to carry only a slot number. If this ever returns true,
    /// that invariant is broken somewhere and the correct response is to drop the completion rather than
    /// apply it to whoever holds the slot now - applying it is how a lifetime bug becomes corruption
    /// instead of a log line. Counted always; the count is printed by SS_IOCP_STATS.
    /// </summary>
    private bool StaleCompletion(IocpOp* op)
    {
        var conn = _conns[op->Slot - 1];
        if (op->Generation == conn.Generation) return false;
        Interlocked.Increment(ref s_staleCompletions);
        return true;
    }

    private static void DumpStats(string tag)
    {
        long zc = Interlocked.Read(ref s_zcTaken), classic = Interlocked.Read(ref s_sendPages);
        if (zc + classic == 0) return;
        long dseg = Interlocked.Read(ref s_zcDeclineSegs), dsum = Interlocked.Read(ref s_zcDeclineSegSum);
        Console.Error.WriteLine($"[iocp-stats:{tag}] zero-copy sends={zc:n0} segments={Interlocked.Read(ref s_zcSegs):n0} " +
            $"prefix-sends={Interlocked.Read(ref s_zcPrefix):n0} " +
            $"| declined: tls={Interlocked.Read(ref s_zcDeclineTls):n0} closed={Interlocked.Read(ref s_zcDeclineClosed):n0} " +
            $"empty={Interlocked.Read(ref s_zcDeclineEmpty):n0} too-fragmented={dseg:n0} " +
            // The ZERO-COPY cap, not MaxSendPages. Printing the wrong one here would be the exact failure
            // this counter exists to prevent: a banner that reports a limit the code is not applying.
            $"(cap={IocpConnection.MaxZeroCopySegments} mean-segs={(dseg > 0 ? (double)dsum / dseg : 0):n2} " +
            $"max-segs={Interlocked.Read(ref s_zcDeclineSegMax):n0}) " +
            $"| copying path: WSASends={classic:n0} WSABUFs={Interlocked.Read(ref s_sendPageBufs):n0} " +
            $"| STALE COMPLETIONS={Interlocked.Read(ref s_staleCompletions):n0} (must be 0)");
    }

    // Periodic as well as at shutdown, for the reason io_uring's is: a rig kills the server, and a
    // measurement that can only be read from a clean shutdown vanishes precisely under load.
    // MUST be declared after ReportStats - static initializers run in declaration order.
    private static readonly Timer? StatsTimer = ReportStats
        ? new Timer(static _ => DumpStats("periodic"), null, 2000, 2000)
        : null;

    /// <summary>A zero-copy send was refused because the sequence has more segments than one WSASend can
    /// carry. Records what the count actually WAS, which is the number the 64-segment cap is judged
    /// against - "it declined" and "it declined at 65" are different findings.</summary>
    private static void RecordFragmentDecline(in ReadOnlySequence<byte> data)
    {
        Interlocked.Increment(ref s_zcDeclineSegs);
        int total = 0;
        foreach (var _ in data) total++;
        Interlocked.Add(ref s_zcDeclineSegSum, total);
        long seen;
        while (total > (seen = Interlocked.Read(ref s_zcDeclineSegMax)))
            if (Interlocked.CompareExchange(ref s_zcDeclineSegMax, total, seen) == seen) break;
    }

    /// <summary>
    /// Accept a zero-copy send, or decline it so the caller copies instead. Called on the outbound pump
    /// thread, not the loop.
    ///
    /// Declines, each for a reason worth stating:
    ///  - TLS. The bytes must be ENCRYPTED before they reach the wire, so handing the socket the
    ///    application's plaintext would put plaintext on the network. This is not a performance
    ///    limitation, it is a correctness one.
    ///  - Connection closing, or a zero-copy send already outstanding.
    ///
    /// A sequence with more segments than one <c>WSASend</c> can carry is NOT a decline: the first
    /// <see cref="IocpConnection.MaxZeroCopySegments"/> segments are sent and the byte count is
    /// returned, so the caller advances by that much and offers the rest. See the base declaration for
    /// why the cliff had to become a slope.
    /// </summary>
    /// <returns>Bytes accepted — possibly a prefix of <paramref name="data"/>; 0 = declined.</returns>
    internal long TrySendZeroCopy(IocpConnection conn, in ReadOnlySequence<byte> data, bool pinned,
                                 out ValueTask<bool> completion)
    {
        completion = default;
        if (conn.Tls is not null) { if (ReportStats) Interlocked.Increment(ref s_zcDeclineTls); return 0; } // must encrypt: see above
        if (Volatile.Read(ref conn.Socket) == 0) { if (ReportStats) Interlocked.Increment(ref s_zcDeclineClosed); return 0; }
        if (data.IsEmpty) { if (ReportStats) Interlocked.Increment(ref s_zcDeclineEmpty); return 0; }

        conn.EnsureZcArrays(needHandles: !pinned);
        nint[] ptrs = conn.ZcPtrs!;
        int[] lens = conn.ZcLens!;
        var handles = pinned ? null : conn.ZcHandles;
        int i = 0;
        long accepted = 0;
        bool truncated = false;
        foreach (var seg in data)
        {
            if (seg.IsEmpty) continue;
            if (i == IocpConnection.MaxZeroCopySegments) { truncated = true; break; } // send a PREFIX
            if (handles is null)
            {
                // Caller asserts the memory is already pinned, so its address is stable without a handle.
                ptrs[i] = (nint)System.Runtime.CompilerServices.Unsafe.AsPointer(
                    ref System.Runtime.InteropServices.MemoryMarshal.GetReference(seg.Span));
            }
            else
            {
                var h = seg.Pin();
                handles[i] = h;
                ptrs[i] = (nint)h.Pointer;
            }
            lens[i] = seg.Length;
            accepted += seg.Length;
            i++;
        }
        if (i == 0) { DisposeZc(handles, i); return 0; }

        // The handle array is now POOLED, so "how many pins are live" can no longer be read off the
        // array being null: a pinned-memory send leaves a previous send's array in place, unused.
        conn.ZcHandleCount = handles is null ? 0 : i;
        conn.ZcCount = i;
        if (ReportStats)
        {
            Interlocked.Increment(ref s_zcTaken);
            Interlocked.Add(ref s_zcSegs, i);
            if (truncated) Interlocked.Increment(ref s_zcPrefix);
        }
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        conn.ZcCompletion = tcs;
        completion = new ValueTask<bool>(tcs.Task);

        _zeroCopy.Enqueue((conn.Slot, Volatile.Read(ref conn.Generation)));
        Poke();
        return accepted;
    }

    private static void DisposeZc(System.Buffers.MemoryHandle[]? handles, int count)
    {
        if (handles is null) return;
        for (int i = 0; i < count; i++) handles[i].Dispose();
    }

    /// <summary>Release a finished/abandoned zero-copy send and signal the pump. Loop thread.</summary>
    private void FinishZeroCopy(IocpConnection conn, bool ok)
    {
        // Dispose the pins but KEEP the array - it is pooled per connection now. ZcHandleCount, not
        // ZcCount, is the live count: a pinned-memory send has ZcCount > 0 and no pins at all.
        DisposeZc(conn.ZcHandles, conn.ZcHandleCount);
        conn.ZcHandleCount = 0;
        conn.ZcCount = 0;
        conn.SendZeroCopy = false;
        conn.ZcPending = false;
        var tcs = conn.ZcCompletion;
        conn.ZcCompletion = null;
        tcs?.TrySetResult(ok);
    }

    /// <summary>Post the in-flight zero-copy send as ONE WSASend over the caller's own segments, resuming
    /// at <c>SendSent</c>. The mirror of <see cref="IssueSendPages"/>, but the buffers are the
    /// application's pipe memory rather than write pages, so nothing is leased or released here.</summary>
    private void IssueSendZeroCopy(IocpConnection conn, uint slot)
    {
        IocpOp* op = SendOp(slot);
        op->Kind = OpKind.Send;
        op->Slot = slot;
        op->Generation = conn.Generation;
        op->Buf = -1; // no write-pool page backs this send

        Win32.WSABUF* bufs = stackalloc Win32.WSABUF[IocpConnection.MaxZeroCopySegments];
        nint[] ptrs = conn.ZcPtrs!;
        int[] lens = conn.ZcLens!;
        int n = 0, skip = conn.SendSent;
        for (int i = 0; i < conn.ZcCount; i++)
        {
            int len = lens[i];
            if (skip >= len) { skip -= len; continue; }
            bufs[n].buf = (byte*)ptrs[i] + skip;
            bufs[n].len = (uint)(len - skip);
            n++;
            skip = 0;
        }
        if (n == 0) { conn.SendBusy = false; FinishZeroCopy(conn, ok: true); return; }

        uint sent = 0;
        int rc = Win32.WSASend(conn.Socket, bufs, (uint)n, &sent, 0, &op->Overlapped, null);
        if (rc == 0)
        {
            if (conn.SkipOnSuccess) QueueInline(OpKind.Send, slot, sent, failed: false);
            return;
        }
        if (Marshal.GetLastPInvokeError() != Win32.WSA_IO_PENDING)
            QueueInline(OpKind.Send, slot, 0, failed: true);
    }

    /// <summary>Start a queued zero-copy send if the connection is idle. Loop thread.</summary>
    private void StartZeroCopy(IocpConnection conn, uint slot)
    {
        if (conn.SendBusy || conn.Closing || conn.Socket == 0) return; // issued from CompleteWrite instead
        conn.ZcPending = false;
        conn.SendZeroCopy = true;
        conn.SendBusy = true;
        conn.SendSent = 0;
        int total = 0;
        for (int i = 0; i < conn.ZcCount; i++) total += conn.ZcLens[i];
        conn.SendTotal = total;
        IssueSendZeroCopy(conn, slot);
    }

    /// <summary>Marshal a close request onto the loop thread (from <see cref="WindowsConnection.Close"/>).</summary>
    public override void SubmitClose(uint slot, uint generation)
    {
        _closes.Enqueue((slot, generation));
        Poke();
    }

    /// <summary>Marshal an out-of-band flushed write onto the loop thread (from <see cref="OutboundConnection.Flush"/>).</summary>
    public override void SubmitFlush(uint slot, uint generation, byte[] data, int length)
    {
        _flush.Enqueue((slot, generation, data, length));
        Poke();
    }

    /// <summary>Marshal a parked-receive resume onto the loop thread (from <see cref="Connection.ResumeReceive"/>,
    /// which runs on whichever thread completed the consumer's flush).</summary>
    public override void SubmitResumeReceive(uint slot, uint generation)
    {
        _resumes.Enqueue((slot, generation));
        Poke();
    }

    // =====================================================================
    // Slot table
    // =====================================================================

    // Claim a free slot for a socket. Loop-thread only (accept adoption, or a connect marshaled via
    // StartConnect) — the single-writer model, so the claim is a plain free-list pop + plain stores, no
    // CAS. The caller reserved first, so a slot is guaranteed; Claim only fails on counter drift / an
    // unreserved caller (backstop → caller releases + drops). Returns null if the table is full.
    private IocpConnection? InitClient(nint socket, object? userToken, SocketSet.SocketFlags flags)
    {
        int idx = _slots.Claim();
        if (idx < 0) return null;
        var conn = _conns[idx];
        conn.UserToken = userToken;
        conn.Flags = flags;
        conn.Opened = false;
        conn.Closing = false;
        conn.RecvArmed = false;
        conn.SendBusy = false;
        conn.SkipOnSuccess = false;
        conn.RecvBuf = -1;
        conn.SendBuf = -1;
        conn.SendPageCount = 0;
        DiscardPending(conn); // returns the buffers AND clears PendingHeadOffset (a stale one corrupts)
        conn.Tls = null;      // disposed by TryFinalize; cleared here so a rolled-back claim starts clean
        conn.IsClient = false;
        conn.StartedTicks = conn.LastActivityTicks = Clock.Millis;
        conn.MaxInboundBufferBytes = Parent.Options.MaxInboundBufferBytes; // deadline clock
        conn.SkipBufferWipe = Parent.Options.DangerousDisableBufferWipe;
        conn.ResetReceiveParking();

        // Bump the generation before publishing Socket: any out-of-band Close/flush captured against the
        // previous tenant now mismatches and is dropped rather than misapplied.
        Volatile.Write(ref conn.Generation, conn.Generation + 1);
        Volatile.Write(ref conn.Socket, socket); // publish live last (foreign readers gate on Socket != 0)
        return conn;
    }


    /// <summary>Begin tearing a connection down (loop thread). Idempotent. Fires OnClosed now, but does
    /// NOT free the slot yet — closesocket aborts the in-flight recv/send, and the slot is finalized
    /// only once those completions have drained (see <see cref="TryFinalize"/>), so no stale completion
    /// lands on a re-tenanted slot.</summary>
    protected override void CloseClient(uint slot)
    {
        if (slot == 0) return;
        var conn = _conns[slot - 1];
        if (conn.Socket == 0 || conn.Closing) return; // free / already tearing down
        conn.Closing = true;

        // Ungated: a failed handshake never set Opened, so this must not hang off DispatchClosed.

        Parent.DispatchTlsFault(conn);


        if (conn.Opened)
        {
            conn.Opened = false;
            try { Parent.DispatchClosed(conn); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
        }

        // shutdown sends the FIN; closesocket aborts the pending recv/send (they complete with an error,
        // which clears RecvArmed/SendBusy). Socket stays non-zero (the now-closed handle) as the claimed
        // marker until TryFinalize publishes the slot free.
        if (Parent.Options.ResetOnClose)
        {
            // Abortive: SO_LINGER{1,0} → closesocket sends RST, no FIN, no TIME_WAIT on the active closer.
            var lg = new Win32.LINGER { l_onoff = 1, l_linger = 0 };
            Win32.setsockopt(conn.Socket, Win32.SOL_SOCKET, Win32.SO_LINGER, &lg, sizeof(Win32.LINGER));
        }
        else
        {
            Win32.shutdown(conn.Socket, Win32.SD_BOTH);
        }
        Win32.closesocket(conn.Socket);
        TryFinalize(conn, slot); // nothing in flight → finalize immediately
    }

    // Finalize once all in-flight ops for a closing slot have drained: release its buffers and publish
    // the slot free LAST (only now may a racing InitClient claim it).
    private void TryFinalize(IocpConnection conn, uint slot)
    {
        if (!conn.Closing || conn.RecvArmed || conn.SendBusy) return;

        if (conn.RecvBuf >= 0) { _recvBuffer.Release(conn.RecvBuf); conn.RecvBuf = -1; }
        ReleaseSendPages(conn); // every page of any send that was still in flight
        // Unpin and release any zero-copy send (in flight or merely queued), or its pump waits forever on
        // a connection that no longer exists.
        if (conn.SendZeroCopy || conn.ZcPending || conn.ZcCompletion is not null) FinishZeroCopy(conn, ok: false);
        // Return any queued (pooled) echo staging buffers before recycling the slot.
        if (conn.Pending is { } pending)
            while (pending.Count > 0) ArrayPool<byte>.Shared.Return(pending.Dequeue().Array!);
        // Release the TLS engine (SSPI context / SSL*) with the rest of the per-connection state.
        if (conn.Tls is { } tls) { conn.Tls = null; tls.Dispose(); }
        conn.UserToken = null;
        conn.Flags = 0;
        conn.Closing = false;
        Volatile.Write(ref conn.Socket, 0); // publish free last (socket already closed in CloseClient)
        _slots.Free((int)(slot - 1));       // return to the loop-local allocator (loop thread only)
        ReleaseReservation();               // paired with the TryReserve that placed this connection
    }

    private IocpOp* RecvOp(uint slot) => &_ops[(slot - 1) * 2];
    private IocpOp* SendOp(uint slot) => &_ops[(slot - 1) * 2 + 1];

    // =====================================================================
    // Public entry points
    // =====================================================================

    public override void Listen(EndPoint endpoint, object? userToken, bool local)
    {
        EnsureWinsock();
        // IOCP has no reuse-port load balancing, so this is always a single listener (the factory reports
        // CanMultiBind == false, so SocketSet.Listen routes each endpoint to just one round-robin shard).
        // This shard drives the accept and bounces each accepted connection round-robin, so `local` is
        // moot here — we always bounce.
        var (listener, af, proto) = CreateListener(endpoint);
        Win32.LoadExtensions(listener);
        if (Win32.CreateIoCompletionPort(listener, _port, 0, 0) == 0)
        {
            Win32.closesocket(listener);
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateIoCompletionPort(listener) failed");
        }

        StartAccept(listener, af, proto, userToken);
    }

    public override void ListenHandle(nint handle, object? userToken)
    {
        EnsureWinsock();
        if (handle == 0 || handle == Win32.INVALID_SOCKET)
            throw new ArgumentOutOfRangeException(nameof(handle), "Invalid socket handle.");
        // Handed-over listener, assumed already bound + listen()ed. We don't know the family for sure;
        // assume TCP/IPv4 for the accept sockets (matches the only bind path we build today).
        Win32.LoadExtensions(handle);
        if (Win32.CreateIoCompletionPort(handle, _port, 0, 0) == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateIoCompletionPort(listener) failed");
        StartAccept(handle, Win32.AF_INET, Win32.IPPROTO_TCP, userToken);
    }

    public override void Connect(EndPoint endpoint, object? userToken)
    {
        EnsureWinsock();
        int af, proto;
        // Every rejection here must release the reservation first, or a refused dial leaks capacity.
        try
        {
            switch (endpoint)
            {
                case IPEndPoint ip:
                    Win32.RequireIPv4(ip, nameof(Connect)); // IPv4-only sockaddr; never truncate an IPv6 address
                    (af, proto) = (Win32.AF_INET, Win32.IPPROTO_TCP);
                    break;
                case UnixDomainSocketEndPoint:
                    (af, proto) = (Win32.AF_UNIX, 0);
                    break;
                default:
                    throw new NotSupportedException($"{nameof(Connect)} on {endpoint.GetType().Name} is not supported.");
            }
        }
        catch { ReleaseReservation(); throw; }

        // This shard holds a reservation (TryPlace took it). Create + bind the socket HERE (thread-agnostic
        // syscalls, so their failures stay synchronous to the caller), then hand the claim + port-assoc +
        // ConnectEx to the loop, keeping the slot table single-writer. Release the reservation on any
        // synchronous failure so a rejected connect doesn't permanently consume capacity.
        nint s = Win32.WSASocketW(af, Win32.SOCK_STREAM, proto, null, 0, Win32.WSA_FLAG_OVERLAPPED);
        if (s == Win32.INVALID_SOCKET) { ReleaseReservation(); throw new Win32Exception(Marshal.GetLastPInvokeError(), "WSASocketW failed"); }
        Win32.LoadExtensions(s);

        int one = 1;
        if (af == Win32.AF_INET)
        {
            Win32.setsockopt(s, Win32.IPPROTO_TCP, Win32.TCP_NODELAY, &one, sizeof(int));
            // ConnectEx requires the socket be explicitly bound first.
            Win32.SockAddrIn any = default;
            any.sin_family = (ushort)Win32.AF_INET;
            Win32.bind(s, &any, 16);
        }
        else
        {
            // ...and it requires that of AF_UNIX too, which this used to skip. ConnectEx on an unbound
            // AF_UNIX socket fails with WSAEINVAL, StartConnect then closes it and frees the slot, and the
            // connect silently never happens - so Windows UDS accepted nothing at all on this backend.
            // Bind to the UNNAMED address (family only, no path): the client end needs an address, not a
            // name, and a named bind would litter the filesystem with a file per outbound connection.
            Win32.SockAddrUn unnamed = default;
            unnamed.sun_family = Win32.AF_UNIX;
            if (Win32.bind(s, &unnamed, sizeof(ushort)) != 0)
            {
                int err = Marshal.GetLastPInvokeError();
                Win32.closesocket(s);
                ReleaseReservation();
                throw new Win32Exception(err, "bind(AF_UNIX, unnamed) failed; ConnectEx requires a bound socket");
            }
        }

        _pendingConnects.Enqueue((s, endpoint, userToken));
        Poke();
    }

    // Loop thread: claim the reserved slot for a marshaled connect, associate the socket with the port,
    // build the target sockaddr into the slot's stable native storage, and post ConnectEx. The reservation
    // is consumed by the claim, or released here on any post-claim failure (which are now async, like
    // accept — the caller has already returned).
    private void StartConnect(nint s, EndPoint endpoint, object? userToken)
    {
        var conn = InitClient(s, userToken, SocketSet.SocketFlags.None);
        if (conn is null) { Win32.closesocket(s); ReleaseReservation(); return; }
        uint slot = conn.Slot;

        if (Win32.CreateIoCompletionPort(s, _port, slot, 0) == 0)
        {
            Win32.closesocket(s);
            FreeSlot(conn);
            return;
        }

        // Build the target sockaddr into this slot's stable native storage (the kernel dereferences it
        // asynchronously once ConnectEx is posted).
        byte* addrPtr = _connectAddrs + (nint)(slot - 1) * AddrStride;
        uint addrLen;
        if (endpoint is IPEndPoint ip)
        {
            var sa = (Win32.SockAddrIn*)addrPtr;
            *sa = default;
            sa->sin_family = (ushort)Win32.AF_INET;
            sa->sin_port = Win32.Htons((ushort)ip.Port);
            var b = ip.Address.GetAddressBytes(); // 4 bytes, network order
            byte* dst = (byte*)&sa->sin_addr;
            dst[0] = b[0]; dst[1] = b[1]; dst[2] = b[2]; dst[3] = b[3];
            addrLen = 16;
        }
        else // UnixDomainSocketEndPoint (caller validated the type before marshaling)
        {
            var uds = (UnixDomainSocketEndPoint)endpoint;
            addrLen = Win32.SockAddrUn.Init((Win32.SockAddrUn*)addrPtr, UnixSocketFile.ValidatePath(uds.ToString()));
        }

        // Connect reuses the slot's recv op-ctx (no recv is armed yet); re-armed as a recv on completion.
        IocpOp* op = RecvOp(slot);
        op->Kind = OpKind.Connect;
        op->Slot = slot;
        op->Generation = conn.Generation;
        op->Buf = 0;

        uint sent = 0;
        int ok = Win32.ConnectEx(s, addrPtr, (int)addrLen, null, 0, &sent, &op->Overlapped);
        if (ok == 0 && Win32.WSAGetLastError() != Win32.WSA_IO_PENDING)
        {
            Win32.closesocket(s);
            FreeSlot(conn);
            return;
        }
        // ok != 0 (immediate) or WSA_IO_PENDING → a completion is queued to the port.
    }

    // Create, bind and listen a Winsock socket for the endpoint. Throws on failure.
    private (nint socket, int af, int proto) CreateListener(EndPoint endpoint)
    {
        switch (endpoint)
        {
            case IPEndPoint ip:
            {
                nint s = Win32.WSASocketW(Win32.AF_INET, Win32.SOCK_STREAM, Win32.IPPROTO_TCP, null, 0, Win32.WSA_FLAG_OVERLAPPED);
                if (s == Win32.INVALID_SOCKET) throw new Win32Exception(Marshal.GetLastPInvokeError(), "WSASocketW failed");
                int one = 1;
                Win32.setsockopt(s, Win32.SOL_SOCKET, Win32.SO_REUSEADDR, &one, sizeof(int));
                Win32.setsockopt(s, Win32.IPPROTO_TCP, Win32.TCP_NODELAY, &one, sizeof(int));
                Win32.RequireIPv4(ip, nameof(SocketSet.Listen));
                Win32.SockAddrIn addr = default;
                addr.sin_family = (ushort)Win32.AF_INET;
                addr.sin_port = Win32.Htons((ushort)ip.Port);
                // Honour the requested address. Was hard-coded to INADDR_ANY (the "TODO: honour the actual
                // IP" the 2026-08-04 audit cashed in): Listen(IPEndPoint(IPAddress.Loopback, p)) listened on
                // every interface, on every backend except managed. See IoUringFactory.Bind for the writeup.
                addr.sin_addr = Win32.ToSinAddr(ip.Address);
                if (Win32.bind(s, &addr, 16) == Win32.SOCKET_ERROR)
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "IP bind() failed");
                if (Win32.listen(s, _listenBacklog) == Win32.SOCKET_ERROR)
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "IP listen() failed");
                return (s, Win32.AF_INET, Win32.IPPROTO_TCP);
            }
            case UnixDomainSocketEndPoint uds:
            {
                nint s = Win32.WSASocketW(Win32.AF_UNIX, Win32.SOCK_STREAM, 0, null, 0, Win32.WSA_FLAG_OVERLAPPED);
                if (s == Win32.INVALID_SOCKET) throw new Win32Exception(Marshal.GetLastPInvokeError(), "WSASocketW(AF_UNIX) failed");
                string udsPath = UnixSocketFile.ValidatePath(uds.ToString()); // '@abstract' is Linux-only
                UnixSocketFile.PrepareForBind(udsPath); // clear a stale socket file (Windows AF_UNIX is filesystem-only)
                Win32.SockAddrUn addr;
                uint len = Win32.SockAddrUn.Init(&addr, udsPath);
                if (Win32.bind(s, &addr, (int)len) == Win32.SOCKET_ERROR)
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "UDS bind(AF_UNIX) failed");
                if (Win32.listen(s, _listenBacklog) == Win32.SOCKET_ERROR)
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "UDS listen() failed");
                return (s, Win32.AF_UNIX, 0);
            }
            default:
                throw new NotSupportedException(endpoint.GetType().Name);
        }
    }

    // Arm a pool of AcceptConcurrency outstanding AcceptEx on the listener — a backlog of accept
    // consumers so connect bursts don't serialize on one accept-at-a-time, and one failed re-post
    // doesn't stall the whole listener. Each completion re-posts its own state (see HandleAccept).
    private void StartAccept(nint listener, int af, int proto, object? token)
    {
        for (int i = 0; i < _acceptConcurrency; i++)
        {
            var st = new AcceptState
            {
                Listener = listener,
                Token = token,
                Af = af,
                Proto = proto,
                Buf = (nint)NativeMemory.AllocZeroed(AcceptBufSize),
                Op = (nint)NativeMemory.AllocZeroed((nuint)sizeof(AcceptOp)),
            };
            st.Gc = GCHandle.Alloc(st);
            ((AcceptOp*)st.Op)->Handle = GCHandle.ToIntPtr(st.Gc);
            lock (_acceptGate) _acceptStates.Add(st);
            PostAccept(st);
        }
    }

    // Create a fresh accept socket and post AcceptEx into the listener. Called on Listen (any thread)
    // and on each accept completion (loop thread); AcceptEx submission is thread-safe either way.
    private void PostAccept(AcceptState st)
    {
        nint acc = Win32.WSASocketW(st.Af, Win32.SOCK_STREAM, st.Proto, null, 0, Win32.WSA_FLAG_OVERLAPPED);
        if (acc == Win32.INVALID_SOCKET)
        {
            System.Diagnostics.Debug.WriteLine($"WSASocketW(accept) failed: {Marshal.GetLastPInvokeError()}");
            st.AcceptSocket = 0;
            return; // accept stalls on this listener; TODO: retry/backoff
        }

        st.AcceptSocket = acc;
        var op = (AcceptOp*)st.Op;
        op->Kind = OpKind.Accept;
        // Handle already set at StartAccept.

        uint recvd = 0;
        int ok = Win32.AcceptEx(st.Listener, acc, (void*)st.Buf, 0, AddrStride, AddrStride, &recvd, &op->Overlapped);
        if (ok == 0 && Win32.WSAGetLastError() != Win32.WSA_IO_PENDING)
        {
            System.Diagnostics.Debug.WriteLine($"AcceptEx failed: {Win32.WSAGetLastError()}");
            Win32.closesocket(acc);
            st.AcceptSocket = 0;
            // TODO: retry/backoff rather than silently stalling this listener.
        }
        // ok != 0 (immediate) or WSA_IO_PENDING → a completion is queued.
    }

    // =====================================================================
    // Completion handlers (loop thread)
    // =====================================================================

    private void HandleAccept(AcceptOp* op, bool failed)
    {
        var st = (AcceptState)GCHandle.FromIntPtr(op->Handle).Target!;
        nint acc = st.AcceptSocket;

        if (failed || acc == 0)
        {
            if (acc != 0) Win32.closesocket(acc);
            PostAccept(st);
            return;
        }

        // Required before an AcceptEx socket can be used: inherit the listener's properties/state.
        nint listener = st.Listener;
        Win32.setsockopt(acc, Win32.SOL_SOCKET, Win32.SO_UPDATE_ACCEPT_CONTEXT, &listener, sizeof(nint));

        // Single acceptor → place on the first shard with a free slot (capacity-aware; drops only if
        // every shard is full).
        var target = (IocpShard?)Parent.TryPlace();
        if (target is not null) target.EnqueueInbound(acc, st.Token);
        else Win32.closesocket(acc); // every shard full → drop (runtime shard growth would expand here)

        PostAccept(st); // keep the listener saturated
    }

    // Associate an accepted socket with THIS shard's port, run OnAccept, arm recv, fire any initial send.
    private void AdoptAccepted(nint socket, object? token)
    {
        int one = 1;
        Win32.setsockopt(socket, Win32.IPPROTO_TCP, Win32.TCP_NODELAY, &one, sizeof(int)); // harmless on AF_UNIX

        var conn = InitClient(socket, token, SocketSet.SocketFlags.None);
        if (conn is null) { Win32.closesocket(socket); ReleaseReservation(); return; }
        uint slot = conn.Slot;

        if (Win32.CreateIoCompletionPort(socket, _port, slot, 0) == 0)
        {
            Win32.closesocket(socket);
            FreeSlot(conn);
            return;
        }

        // Handle synchronous recv/send completions inline (skip the completion-port round-trip). If the
        // flag is rejected, SkipOnSuccess stays false and the socket keeps the always-async model.
        conn.SkipOnSuccess = Win32.SetFileCompletionNotificationModes(socket,
            (byte)(Win32.FILE_SKIP_COMPLETION_PORT_ON_SUCCESS | Win32.FILE_SKIP_SET_EVENT_ON_HANDLE));

        if (!_recvBuffer.TryLease(out int ri, out _))
        {
            // Recv pool exhausted (should not happen: sized to the connection table). Drop the connection.
            Win32.closesocket(socket);
            FreeSlot(conn);
            return;
        }
        conn.RecvBuf = ri;

        // TLS: the app must not see this connection until the handshake completes, so OnAccept is deferred
        // to FireTlsOpen and everything below is skipped.
        var serverTls = Parent.ResolveServerTls(conn);
        if (serverTls.Refused) { CloseClient(slot); return; }   // never downgrade to plaintext
        if (serverTls.Enabled) { BeginTls(conn, slot, isClient: false, serverTls); return; }

        bool leased = _writeBuffer.TryLease(out int wi, out byte* wp);
        var ctx = new SocketSet.AcceptContext(conn, wp, leased ? _writeBufSize : 0);
        conn.Opened = true;
        Parent.DispatchAccept(ref ctx);

        ArmRecvIfWanted(conn);

        int sb = ctx.SendBytes;
        if (leased && sb > 0 && conn.Socket != 0 && !conn.Closing) SubmitSendBuffer(conn, slot, wi, sb);
        else if (leased) _writeBuffer.Release(wi);
    }

    private void HandleConnect(uint slot, bool failed)
    {
        var conn = _conns[slot - 1];
        if (failed || conn.Socket == 0) { CloseClient(slot); return; }

        Win32.setsockopt(conn.Socket, Win32.SOL_SOCKET, Win32.SO_UPDATE_CONNECT_CONTEXT, null, 0);

        // Handle synchronous recv/send completions inline (see AdoptAccepted). Set after connect
        // completed, so ConnectEx itself stayed on the always-async path.
        conn.SkipOnSuccess = Win32.SetFileCompletionNotificationModes(conn.Socket,
            (byte)(Win32.FILE_SKIP_COMPLETION_PORT_ON_SUCCESS | Win32.FILE_SKIP_SET_EVENT_ON_HANDLE));

        if (!_recvBuffer.TryLease(out int ri, out _)) { CloseClient(slot); return; }
        conn.RecvBuf = ri;

        // TLS: OnConnect is deferred to FireTlsOpen (the client speaks first — see BeginTls).
        var clientTls = Parent.ResolveClientTls(conn);
        if (clientTls.Refused) { CloseClient(slot); return; }   // never downgrade to plaintext
        if (clientTls.Enabled) { BeginTls(conn, slot, isClient: true, clientTls); return; }

        bool leased = _writeBuffer.TryLease(out int wi, out byte* wp);
        var ctx = new SocketSet.ConnectContext(conn, wp, leased ? _writeBufSize : 0);
        conn.Opened = true;
        Parent.DispatchConnect(ref ctx);

        ArmRecvIfWanted(conn);

        int sb = ctx.SendBytes;
        if (leased && sb > 0 && conn.Socket != 0 && !conn.Closing) SubmitSendBuffer(conn, slot, wi, sb);
        else if (leased) _writeBuffer.Release(wi);
    }

    private void HandleRecv(uint slot, uint bytes, bool failed)
    {
        var conn = _conns[slot - 1];

        if (conn.Closing) { conn.RecvArmed = false; TryFinalize(conn, slot); return; }
        if (failed || bytes == 0)
        {
            // error, or graceful EOF (0 bytes). RecvArmed cleared first so CloseClient's TryFinalize can
            // proceed once the send (if any) also drains.
            conn.RecvArmed = false;
            CloseClient(slot);
            return;
        }

        // RecvArmed stays TRUE across DeliverReceive so that if it closes the connection (e.g. write pool
        // exhausted), TryFinalize won't finalize (and re-tenant) the slot out from under us here.
        bool keep = DeliverReceive(conn, slot, (int)bytes);

        if (keep && conn.Socket != 0 && !conn.Closing && (conn.Flags & SocketSet.SocketFlags.ReceiveClosed) == 0)
        {
            // PARKING (REVIEW.md D3): the consumer called TryPauseReceive from inside the callback because
            // it is behind. Simply do not post the next WSARecv — the socket's receive buffer fills, the
            // advertised window closes, and the PEER slows down instead of being dropped at a cap.
            //
            // RecvArmed MUST clear here even though nothing failed. It means "an operation is outstanding"
            // and it is what defer-recycle waits on; leaving it set would hang a subsequent close forever
            // on a completion that is never coming. Nothing else needs guarding, because a parked slot has
            // no in-flight receive to land on it.
            if (conn.TryParkReceive()) { conn.RecvArmed = false; return; }
            ArmRecv(conn); // re-arm (RecvArmed remains true)
        }
        else
        {
            conn.RecvArmed = false;   // this recv op is done; no re-arm
            TryFinalize(conn, slot);  // finalize now if closing and the send has drained
        }
    }

    // Dispatch OnReceive and, if it set a response, send it (copy through the write pool). Returns false
    // only if it tore the connection down (so the caller stops receiving).
    private bool DeliverReceive(IocpConnection conn, uint slot, int bytes)
    {
        if (conn.Tls is not null) return DeliverReceiveTls(conn, slot, bytes);

        byte* rp = _recvBuffer.Address(conn.RecvBuf);
        var ctx = new SocketSet.ReceiveContext(conn, rp, _recvBufSize, bytes);
        Parent.DispatchReceive(ref ctx);

        int rb = ctx.ResponseBytes;
        if (rb <= 0 || (conn.Flags & SocketSet.SocketFlags.SendClosed) != 0) return true;

        if (conn.SendBusy)
        {
            // A send is already in flight: stash the response and queue it behind the current one. The
            // staging buffer is pooled (loop-thread rent/return hits the per-thread cache), so a pipelined
            // echo doesn't allocate per message; it's returned when drained (CompleteWrite) or dropped
            // (TryFinalize). Rent may over-size, so the ArraySegment carries the true length.
            var copy = ArrayPool<byte>.Shared.Rent(rb);
            Marshal.Copy((nint)rp, copy, 0, rb);
            (conn.Pending ??= new()).Enqueue(new ArraySegment<byte>(copy, 0, rb));
            return true;
        }

        return SendResponse(conn, slot, rp, rb);
    }

    // Copy a response into a leased write buffer and send it. Closes (returns false) if no buffer is free.
    private bool SendResponse(IocpConnection conn, uint slot, byte* src, int len)
    {
        if (!_writeBuffer.TryLease(out int wi, out byte* wp))
        {
            // Pool dry: stage the bytes and retry on a later pass instead of tearing down a healthy
            // connection. See WindowsShardBase._awaitingPage.
            StageOutbound(conn, new ReadOnlySpan<byte>(src, len));
            MarkAwaitingPage(conn);
            return true;
        }

        Buffer.MemoryCopy(src, wp, _writeBufSize, len);
        SubmitSendBuffer(conn, slot, wi, len); // sets SendBusy; closes on synchronous failure
        return !conn.Closing;
    }

    // Send the whole of an already-filled write buffer (initial send / echo) as a one-page send.
    private void SubmitSendBuffer(IocpConnection conn, uint slot, int wi, int len)
    {
        conn.SendPages[0] = wi;
        conn.SendLens[0] = len;
        conn.SendPageCount = 1;
        conn.SendBuf = wi;
        conn.SendSent = 0;
        conn.SendTotal = len;
        conn.SendBusy = true;
        IssueSendPages(conn, slot);
    }

    /// <summary>
    /// Post the in-flight send as ONE WSASend over all its pages, resuming at <c>SendSent</c>. This is
    /// the whole point of the page array: a 256KB response is one call with 64 WSABUFs rather than 64
    /// sequential calls. On a synchronous outcome (success with FILE_SKIP, or any failure) no packet
    /// posts, so the completion is deferred inline - a synchronous failure flows through as
    /// HandleSend(failed) -> FailSend, exactly like an async error.
    /// </summary>
    private void IssueSendPages(IocpConnection conn, uint slot)
    {
        IocpOp* op = SendOp(slot);
        op->Kind = OpKind.Send;
        op->Slot = slot;
        op->Generation = conn.Generation;
        op->Buf = conn.SendPages[0];

        // Skip whatever a previous partial send already delivered, then describe the remainder.
        Win32.WSABUF* bufs = stackalloc Win32.WSABUF[IocpConnection.MaxSendPages];
        int n = 0, skip = conn.SendSent;
        for (int i = 0; i < conn.SendPageCount; i++)
        {
            int len = conn.SendLens[i];
            if (skip >= len) { skip -= len; continue; } // this page is fully acknowledged
            bufs[n].buf = _writeBuffer.Address(conn.SendPages[i]) + skip;
            bufs[n].len = (uint)(len - skip);
            n++;
            skip = 0;
        }
        if (n == 0) { CompleteWrite(conn, slot); return; } // nothing left outstanding
        if (ReportStats) { Interlocked.Increment(ref s_sendPages); Interlocked.Add(ref s_sendPageBufs, n); }

        uint sent = 0;
        int rc = Win32.WSASend(conn.Socket, bufs, (uint)n, &sent, 0, &op->Overlapped, null);
        if (rc == 0)
        {
            if (conn.SkipOnSuccess) QueueInline(OpKind.Send, slot, sent, failed: false);
            return;
        }
        if (Marshal.GetLastPInvokeError() != Win32.WSA_IO_PENDING)
            QueueInline(OpKind.Send, slot, 0, failed: true);
        // WSA_IO_PENDING → an async completion will arrive.
    }

    /// <summary>Release every page of the in-flight send and mark the connection idle.</summary>
    private void ReleaseSendPages(IocpConnection conn)
    {
        for (int i = 0; i < conn.SendPageCount; i++) _writeBuffer.Release(conn.SendPages[i]);
        conn.SendPageCount = 0;
        conn.SendBuf = -1;
    }

    /// <summary>
    /// Pack queued responses into the send pages, spilling into freshly-leased pages as needed. Packing
    /// rather than one-segment-per-page is what keeps a pipelined echo cheap: several small responses
    /// still coalesce into a single page, and only a large run spills. Returns the bytes added.
    /// </summary>
    private int DrainPendingIntoPages(IocpConnection conn)
    {
        if (conn.Pending is not { Count: > 0 } pending) return 0;
        int added = 0;
        while (pending.Count > 0)
        {
            var seg = pending.Peek();
            // A segment may now be LARGER than a page: StageOutboundOwned hands over the whole ciphertext
            // buffer rather than pre-chunking it, so the head is consumed across as many pages as it takes.
            int off = conn.PendingHeadOffset;
            int remain = seg.Count - off;
            int pi = conn.SendPageCount - 1;
            int used = pi >= 0 ? conn.SendLens[pi] : _writeBufSize; // no page yet -> force a lease
            if (pi < 0 || used >= _writeBufSize)
            {
                if (conn.SendPageCount >= IocpConnection.MaxSendPages) break; // cap this send; rest follows
                if (!_writeBuffer.TryLease(out int wi, out _)) break;         // pool dry; send what we have
                pi = conn.SendPageCount++;
                conn.SendPages[pi] = wi;
                conn.SendLens[pi] = 0;
                used = 0;
            }
            int n = Math.Min(remain, _writeBufSize - used);
            Marshal.Copy(seg.Array!, seg.Offset + off, (nint)(_writeBuffer.Address(conn.SendPages[pi]) + used), n);
            conn.SendLens[pi] = used + n;
            added += n;
            if (n == remain)
            {
                pending.Dequeue();
                conn.PendingHeadOffset = 0;
                ArrayPool<byte>.Shared.Return(seg.Array!);
            }
            else
            {
                // Partly copied: keep it at the head and resume at the offset on the next page/pass. The
                // send cap and a dry pool both land here, so the remainder must survive the loop exit.
                conn.PendingHeadOffset = off + n;
            }
        }
        return added;
    }

    // A send failed synchronously: release its buffers, clear the send slot, tear the connection down.
    private void FailSend(IocpConnection conn, uint slot)
    {
        if (conn.SendZeroCopy) FinishZeroCopy(conn, ok: false); else ReleaseSendPages(conn);
        conn.SendBusy = false;
        CloseClient(slot);
    }

    private void HandleSend(uint slot, uint bytes, bool failed)
    {
        var conn = _conns[slot - 1];

        if (conn.Closing)
        {
            if (conn.SendZeroCopy) FinishZeroCopy(conn, ok: false); else ReleaseSendPages(conn);
            conn.SendBusy = false;
            TryFinalize(conn, slot);
            return;
        }

        if (failed) { FailSend(conn, slot); return; }

        conn.SendSent += (int)bytes;
        if (conn.SendSent < conn.SendTotal)
        {
            if (bytes == 0) { FailSend(conn, slot); return; } // no progress → dead peer
            // Partial send: re-post the remainder. Both issuers skip the acknowledged prefix across
            // their buffers, so a partial write that lands mid-buffer resumes at the right byte.
            if (conn.SendZeroCopy) IssueSendZeroCopy(conn, slot); else IssueSendPages(conn, slot);
            return;
        }

        if (conn.SendZeroCopy)
        {
            // Nothing to hand back to the write pool, and no OnWrite/echo drain: in pipe mode the pump
            // owns the outbound half. Unpin, release the pump, and go idle — the pump's next ReadAsync is
            // what produces the next send.
            conn.SendBusy = false;
            FinishZeroCopy(conn, ok: true);
            return;
        }

        CompleteWrite(conn, slot);
    }

    // A send fully completed. Offer the freed buffer to OnWrite (pipeline the next message straight back
    // into it); failing that, drain a queued echo into it; failing both, release it and go idle.
    private void CompleteWrite(IocpConnection conn, uint slot)
    {
        // Keep page 0 (OnWrite needs somewhere to write); hand the rest back now that they are on the wire.
        for (int i = 1; i < conn.SendPageCount; i++) _writeBuffer.Release(conn.SendPages[i]);
        conn.SendPageCount = 1;
        conn.SendLens[0] = 0;
        int wi = conn.SendPages[0];
        conn.SendBuf = wi;
        byte* wp = _writeBuffer.Address(wi);

        // On a TLS connection OnWrite is suppressed until the deferred open has fired: until then the app
        // has never seen this connection and must not be asked to fill a buffer for it.
        int next = 0;
        var tls = conn.Tls;
        if (tls is null || conn.Opened)
        {
            var ctx = new SocketSet.WriteContext(conn, wp, _writeBufSize);
            Parent.DispatchWrite(ref ctx);
            next = ctx.SendBytes;
        }

        if (tls is not null && next > 0)
        {
            // OnWrite produced PLAINTEXT in the write page. Encrypt it onto the TAIL of Pending rather than
            // sending the page as-is, so it stays ordered behind ciphertext already queued; the drain below
            // then refills this same page from the head. (Records are sequence-numbered — order is not
            // cosmetic here.)
            _tlsCipher!.Reset();
            tls.ProcessOutbound(new ReadOnlySpan<byte>(wp, next), _tlsCipher);
            StageCipher(conn);
            next = 0;
        }

        if (next > 0)
        {
            // OnWrite filled page 0 directly; send it as a one-page send.
            conn.SendLens[0] = next;
            conn.SendSent = 0;
            conn.SendTotal = next; // reuse page 0; SendBusy stays set
            IssueSendPages(conn, slot);
            return;
        }

        // Coalesce queued responses. This is the batching lever under pipelining - it cuts send syscalls
        // N:1 and, because the peer then drains a bigger chunk per recv, its recv-op count too. It now
        // also SPILLS past one page, so a large queued run goes out as one multi-buffer WSASend instead
        // of ceil(size/page) sequential ones.
        int total = DrainPendingIntoPages(conn);
        if (total > 0)
        {
            conn.SendSent = 0;
            conn.SendTotal = total;
            IssueSendPages(conn, slot);
        }
        else
        {
            ReleaseSendPages(conn);
            conn.SendBusy = false;
            // A zero-copy send that arrived while this one was in flight waits here for the connection to
            // go idle, since only one send may be outstanding on a stream socket.
            if (conn.ZcPending) StartZeroCopy(conn, slot);
        }
    }



    // Start draining Pending into freshly-leased write pages (precondition: !SendBusy). Used to kick an
    // out-of-band flush when the connection is otherwise idle; spills across pages exactly as
    // CompleteWrite does, so a large flush is one multi-buffer WSASend.
    protected override void StartPendingSend(IocpConnection conn, uint slot)
    {
        if (conn.Pending is not { Count: > 0 }) return;
        conn.SendPageCount = 0;
        int total = DrainPendingIntoPages(conn);
        if (total == 0)
        {
            // Pool dry (DrainPendingIntoPages only fails to place the FIRST segment when it is). Leave the
            // bytes staged in Pending and retry on a later pass; this used to close the connection.
            ReleaseSendPages(conn);
            MarkAwaitingPage(conn);
            return;
        }
        conn.SendBuf = conn.SendPages[0];
        conn.SendSent = 0;
        conn.SendTotal = total;
        conn.SendBusy = true;
        IssueSendPages(conn, slot);
    }


    // =====================================================================
    // TLS interception (see TlsFilter)
    // -------------------------------------------------------------------------------------
    // This backend has ONE loop thread per shard and every filter call below runs on it, so — unlike the
    // managed fallback, which needs a per-connection gate — there is no locking here and the scratch
    // writers are shared shard-wide.
    //
    // The integration point is the existing Pending queue: ALL ciphertext (handshake flights, control
    // records, encrypted application data) is staged there and drained by the normal send machinery. That
    // is what keeps records in the order the engine produced them — TLS records are sequence-numbered, so
    // the direct SendResponse path, which would jump ahead of anything already queued, must never be used
    // on a TLS connection.
    // =====================================================================

    // Attach a fresh engine to a just-adopted connection and start the handshake. OnAccept/OnConnect are
    // NOT fired here — they fire from FireTlsOpen once the handshake completes.
    private void BeginTls(IocpConnection conn, uint slot, bool isClient, in SocketSets.Tls.TlsResolution tls)
    {
        conn.IsClient = isClient;
        conn.Tls = isClient ? tls.Provider!.CreateClientFilter(tls.Client!) : tls.Provider!.CreateServerFilter(tls.Server!);

        // A client speaks first (ClientHello); a server emits nothing until it has seen one. Either way the
        // receive must be armed so the handshake can advance as bytes arrive.
        if (!DriveTlsHandshake(conn, slot, default)) return;
        ArmRecvIfWanted(conn);
    }






    /// <summary>Arm the receive unless the input half is shut, or the consumer has asked us to park
    /// (REVIEW.md D3). The three open-a-connection paths and the handshake path all go through here so
    /// that a <c>TryPauseReceive</c> issued from OnAccept/OnConnect is honoured rather than overrun.</summary>
    private void ArmRecvIfWanted(IocpConnection conn)
    {
        if ((conn.Flags & SocketSet.SocketFlags.ReceiveClosed) != 0) return;
        if (conn.TryParkReceive()) { conn.RecvArmed = false; return; }
        ArmRecv(conn);
    }

    /// <summary>A parked receive was resumed from off-loop; re-arm it here, on the loop thread.
    /// Generation-guarded like every other marshaled request, and gated on <c>RecvArmed</c> rather than on
    /// the park state: that flag is the loop's own record of whether an operation is outstanding, so it is
    /// what makes a duplicate or late resume a no-op instead of a second WSARecv on one buffer.</summary>
    private void ResumeRecv(uint slot, uint generation)
    {
        var conn = _conns[slot - 1];
        if (conn.Generation != generation || conn.Socket == 0 || conn.Closing || conn.RecvArmed) return;
        if ((conn.Flags & SocketSet.SocketFlags.ReceiveClosed) != 0) return;
        ArmRecv(conn);
    }

    private void ArmRecv(IocpConnection conn)
    {
        uint slot = conn.Slot;
        IocpOp* op = RecvOp(slot);
        op->Kind = OpKind.Recv;
        op->Slot = slot;
        op->Generation = conn.Generation;
        op->Buf = conn.RecvBuf;

        Win32.WSABUF b; b.len = (uint)_recvBufSize; b.buf = _recvBuffer.Address(conn.RecvBuf);
        uint flags = 0, recvd = 0;
        conn.RecvArmed = true;
        int rc = Win32.WSARecv(conn.Socket, &b, 1, &recvd, &flags, &op->Overlapped, null);
        if (rc == 0)
        {
            // Synchronous success: with FILE_SKIP no packet posts, so defer the completion inline.
            // Without it (SkipOnSuccess false) a packet WILL post — do nothing and let it arrive.
            if (conn.SkipOnSuccess) QueueInline(OpKind.Recv, slot, recvd, failed: false);
            return;
        }
        if (Marshal.GetLastPInvokeError() != Win32.WSA_IO_PENDING)
            QueueInline(OpKind.Recv, slot, 0, failed: true); // synchronous failure never posts a packet
        // WSA_IO_PENDING → an async completion will arrive.
    }

    // Pin the loop thread to a core (best-effort). The base Run() pins on Linux; Windows is done here
    // since it needs SetThreadAffinityMask.
    private void PinLoopThread()
    {
        if (!Parent.Options.PinWorkerThreads || !OperatingSystem.IsWindows()) return;
        nuint mask = ChooseAffinityMask();
        if (mask != 0) Win32.SetThreadAffinityMask(Win32.GetCurrentThread(), mask);
    }

    // Pick the Shard-th CPU among those the PROCESS is allowed to run on — respecting a restriction
    // applied via a job object, `start /affinity`, or SetProcessAffinityMask — so pinning stays inside
    // the permitted set. This matches the Linux path (PinCurrentThreadToNthAllowedCpu), which pins to
    // the Nth CPU of the inherited cpuset rather than the Nth absolute core. Returns a single-bit mask,
    // or 0 if the process mask can't be read and no fallback applies (caller then leaves it unpinned).
    // NOTE: single processor group only (<= 64 CPUs); boxes with more use processor groups, which
    // SetThreadAffinityMask can't span — a later refinement if we ever run on such hardware.
    private nuint ChooseAffinityMask()
    {
        nuint proc, sys;
        if (!Win32.GetProcessAffinityMask(Win32.GetCurrentProcess(), &proc, &sys) || proc == 0)
            return (nuint)1 << (Shard % Environment.ProcessorCount); // can't read it → best-effort absolute

        int bits = sizeof(nuint) * 8;
        int allowed = 0;
        for (int b = 0; b < bits; b++)
            if ((proc & ((nuint)1 << b)) != 0) allowed++;
        if (allowed == 0) return 0;

        int target = Shard % allowed; // wrap when there are more shards than allowed CPUs
        int seen = 0;
        for (int b = 0; b < bits; b++)
        {
            if ((proc & ((nuint)1 << b)) == 0) continue;
            if (seen == target) return (nuint)1 << b;
            seen++;
        }
        return 0;
    }
}
#endif
