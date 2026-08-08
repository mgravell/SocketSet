#if NET // Windows IOCP/RIO backends; compiled out of the netfx fallback build.
using System.Buffers;
using SocketSets.Tls;

namespace SocketSets.Windows;

/// <summary>
/// State and behaviour shared by <c>IocpShard</c> and <c>WindowsRioShard</c>. Both run ONE loop thread
/// per shard over a fixed slot table, stage outbound bytes through the same Pending queue, and terminate
/// TLS the same way — so that lives here and each backend keeps only its own I/O primitive, completion
/// pump, and setup/teardown.
///
/// SCOPE, and why it is smaller than it first looks. This class deliberately does NOT own the send state
/// machine. The two used to be near-identical, but they diverged when IOCP gained a send PAGE ARRAY (one
/// WSASend over up to 64 WSABUFs) and RIO could not follow: Windows caps RIOCreateRequestQueue's
/// maxSendDataBuffers at 1 and returns WSAEINVAL above it, so one RIOSend is one contiguous buffer,
/// permanently (established 2026-07-27). <c>CompleteWrite</c> and <c>StartPendingSend</c> differ because
/// the operating system makes them differ, and forcing them together would mean giving RIO a shape it
/// cannot have. What IS shared is the TLS block, outbound staging, the out-of-band flush pump and slot
/// release — all byte-identical before this refactor.
///
/// The two abstract members are per-EVENT (a teardown, a flush kick), never per-operation: the hot
/// primitives (issue-a-send, arm-a-receive, drain-completions) stay entirely in the derived classes and
/// are never reached through this type, so nothing here puts a virtual call on the per-op path.
/// </summary>
/// <typeparam name="TConn">The backend's connection type, so the slot table stays strongly typed and
/// derived code indexes it without a cast.</typeparam>
internal abstract unsafe class WindowsShardBase<TConn> : SocketSetShard, IWindowsShard
    where TConn : WindowsConnection
{
    // --- slot table ---
    // One pooled instance per slot, reused across connection lifetimes so accept/connect never allocates.
    // The slot count is a hard cap on concurrent connections per shard. A Connection is therefore a LEASE
    // on a slot, not an ownable object — a reference held past OnClosed may by then be a different logical
    // connection that reused the slot. Reuse is safe because every use is gated by the per-slot Generation
    // token: Close/writes capture it and are dropped, not misdelivered, if the slot was re-tenanted.
    // Allocated by the derived constructor, which is the only thing that knows how to build a TConn.
    protected TConn[] _conns = null!;

    /// <summary>Drop connections past their deadline (REVIEW.md D2). Shared by IOCP and RIO: both keep a
    /// flat slot table here, so the scan and the close hook are the only per-backend parts.
    /// BLIND: written on Linux, unrun on Windows -- see TODO's Windows catch-up.</summary>
    protected override void SweepTimeouts(long nowTicks)
    {
        for (int i = 0; i < _conns.Length; i++)
        {
            var conn = _conns[i];
            if (conn is null || conn.Socket == 0 || conn.Closing) continue;
            if (Parent.ExpiryReason(conn, nowTicks) is not { } reason) continue;
            Parent.DispatchTimeout(conn, reason);
            CloseClient((uint)(i + 1));
        }
    }

    // Loop-local free-slot allocator (claim/free, single-writer). NOT readonly: it is a mutable struct, so
    // a readonly field would mutate a throwaway copy.
    protected SlotAllocator _slots;

    // --- options snapshot ---
    protected readonly int _socketsPerShard;
    protected readonly int _writeCount;
    protected readonly int _writeBufSize;
    protected readonly int _recvCount;
    protected readonly int _recvBufSize;
    protected readonly int _listenBacklog;
    protected readonly int _acceptConcurrency;

    // --- created on the loop thread in OnInitialize ---
    protected nint _port;                        // IOCP completion port (RIO rides one too, for notify)
    protected PinnedWriteBufferPool _writeBuffer;
    protected PinnedWriteBufferPool _recvBuffer;

    // TLS scratch, shared by every connection on this shard (null unless Options.Tls is set). Safe to
    // share because a shard has ONE loop thread and a filter is only ever touched from it — the managed
    // backend needs a per-connection gate precisely because it has no such thread.
    protected PooledBufferWriter? _tlsPlain;   // decrypt target
    protected PooledBufferWriter? _tlsCipher;  // encrypt scratch
    protected PooledBufferWriter? _tlsCtrl;    // handshake / control-record output

    protected WindowsShardBase(SocketSetOptions options)
    {
        _socketsPerShard = options.SocketsPerShard;
        _writeCount = options.WriteBuffersPerShard;
        _writeBufSize = options.BufferPageSize;
        // Receive buffers are per-socket and held for the connection lifetime, so their size multiplies by
        // SocketsPerShard; the send page does not. See SocketSetOptions.ReceiveBufferSize.
        _recvBufSize = options.ReceiveBufferSize > 0 ? options.ReceiveBufferSize : options.BufferPageSize;
        _recvCount = _socketsPerShard;   // one recv buffer per connection (recv is always armed)
        _listenBacklog = options.ListenBacklog;
        _acceptConcurrency = Math.Max(1, Math.Min(options.AcceptConcurrency, _socketsPerShard));
        // Everything native is deferred to OnInitialize (loop thread); the ctor stays inert so the factory
        // can be constructed on any OS.
    }

    // --- what the shared code needs from the backend (per-event, never per-operation) ---

    /// <summary>Begin teardown of this slot's connection.</summary>
    protected abstract void CloseClient(uint slot);

    /// <summary>Kick a send for a connection that is idle but has queued Pending segments.</summary>
    protected abstract void StartPendingSend(TConn conn, uint slot);

    // Cross-thread submission surface (IWindowsShard) — implemented per backend because each owns its own
    // queues and wake mechanism.
    public abstract void SubmitClose(uint slot, uint generation);
    public abstract void SubmitFlush(uint slot, uint generation, byte[] data, int length);
    public abstract void SubmitResumeReceive(uint slot, uint generation);

    /// <summary>
    /// Fast path for <see cref="SubmitFlush"/> when the caller is ALREADY the loop thread (the common
    /// case: a response written from <c>OnReceive</c>). Pumps the bytes inline, skipping both the
    /// cross-thread queue and the wake that follows it.
    ///
    /// Returns false — meaning "use <see cref="SubmitFlush"/> instead" — whenever it cannot prove the
    /// inline order is the correct one. Two conditions, and the second is the subtle one:
    /// <list type="bullet">
    /// <item>not on the loop thread, so loop-owned state is not ours to touch; and</item>
    /// <item>the flush queue is NOT empty, because a flush enqueued by another thread BEFORE this call
    /// must go out first — jumping it would reorder a connection's byte stream. A flush that arrives
    /// after the check is genuinely concurrent with this one, so no ordering guarantee exists to break.
    /// The queue is shard-wide rather than per-connection, so this declines for an unrelated
    /// connection's pending flush too: conservative, and correctness is worth more than the hop.</item>
    /// </list>
    /// </summary>
    public abstract bool TryFlushOnLoop(uint slot, uint generation, byte[] data, int length);

    /// <summary>
    /// Measurement escape hatch for the fast path above: <c>SS_NO_ONLOOP_FLUSH=1</c> forces every flush
    /// back through the cross-thread queue. It exists so the A/B runs on ONE binary in ONE session
    /// rather than across a rebuild (house rule 6) — and so the fast path can be FALSIFIED rather than
    /// assumed, which a change that only ever runs one way cannot be.
    /// </summary>
    protected static readonly bool OnLoopFlushDisabled =
        Environment.GetEnvironmentVariable("SS_NO_ONLOOP_FLUSH") == "1";

    // --- write-page backpressure ---
    // Connections that wanted a write page while the pool was dry. Their bytes are staged in Pending and
    // retried on a later loop pass.
    //
    // This replaces closing the connection. Running the write pool dry used to call CloseClient, so a
    // transient shortage dropped a HEALTHY connection rather than slowing it down — and Debug.WriteLine
    // made it invisible in Release. Measured 2026-07-27, the defaults dropped 208 connections at
    // -c 2048 with 256KB responses; every configuration with a larger write page dropped 0-1, so a
    // page-size change would have masked this rather than fixed it.
    private readonly List<TConn> _awaitingPage = [];

    /// <summary>Queue a connection for retry once a write page frees. Idempotent.</summary>
    protected void MarkAwaitingPage(TConn conn)
    {
        if (conn.AwaitingPage) return;
        conn.AwaitingPage = true;
        _awaitingPage.Add(conn);
    }

    /// <summary>Retry connections that were waiting on a write page. Called once per loop pass: pages are
    /// released by whichever connection finishes a send, which is not necessarily one that is waiting, so
    /// a per-release hook would have to fan out to every waiter anyway.</summary>
    protected void DrainAwaitingPage()
    {
        for (int i = _awaitingPage.Count - 1; i >= 0; i--)
        {
            var conn = _awaitingPage[i];
            if (conn.Socket == 0 || conn.Closing || conn.Pending is not { Count: > 0 })
            {
                conn.AwaitingPage = false;
                _awaitingPage.RemoveAt(i);
                continue;
            }
            if (conn.SendBusy) continue; // a send is running; CompleteWrite will drain Pending for it

            conn.AwaitingPage = false;
            _awaitingPage.RemoveAt(i);
            StartPendingSend(conn, conn.Slot); // re-marks itself if the pool is still dry
        }
    }

    // --- slot lifecycle ---

    /// <summary>Publish the slot free, return it to the allocator, and release the reservation.</summary>
    protected void FreeSlot(TConn conn)
    {
        Volatile.Write(ref conn.Socket, 0);
        _slots.Free((int)(conn.Slot - 1));
        ReleaseReservation();
    }

    // --- outbound staging ---

    /// <summary>Chunk bytes into write-page-sized pooled segments on Pending (the shape the echo path
    /// already drains). Page-sized chunks preserve the drain loops' "the first item always fits"
    /// invariant.</summary>
    protected void StageOutbound(TConn conn, ReadOnlySpan<byte> data)
    {
        var pending = conn.Pending ??= new();
        for (int off = 0; off < data.Length;)
        {
            int n = Math.Min(_writeBufSize, data.Length - off);
            var buf = ArrayPool<byte>.Shared.Rent(n); // pooled staging (uniform with echo; returned on drain)
            data.Slice(off, n).CopyTo(buf);
            pending.Enqueue(new ArraySegment<byte>(buf, 0, n));
            off += n;
        }
    }

    /// <summary>
    /// Stage an ALREADY-POOLED buffer by taking ownership of it, with no copy and no chunking. The drain
    /// returns it to <see cref="ArrayPool{T}"/>, exactly as for <see cref="StageOutbound"/>'s own rentals.
    ///
    /// This exists because the TLS send path was measured (2026-08-01) to copy every ciphertext byte
    /// TWICE — once here into page-sized pooled chunks, and again in the drain into the write pages — on
    /// top of one <see cref="ArrayPool{T}"/> rent per page. At a 4 KB page a 256 KB response paid 65 rents
    /// and ~525 KB of memcpy that the plaintext path (zero-copy send) does not pay at all. The encrypt
    /// scratch is already a pooled array of exactly the right bytes, so handing it over removes the first
    /// copy and all but one of the rents outright.
    ///
    /// ONLY safe where the drain can consume a segment LARGER than one write page — it no longer chunks,
    /// so a caller whose drain assumes "a segment fits in a page" would truncate. <see cref="Windows.IocpShard"/>
    /// handles this via <see cref="WindowsConnection.PendingHeadOffset"/>; RIO does not, which is why this
    /// is opt-in per backend rather than a change to <see cref="StageOutbound"/>.
    /// </summary>
    protected void StageOutboundOwned(TConn conn, byte[] buffer, int length)
    {
        if (length <= 0) { ArrayPool<byte>.Shared.Return(buffer); return; }
        (conn.Pending ??= new()).Enqueue(new ArraySegment<byte>(buffer, 0, length));
    }

    /// <summary>Whether this backend's drain can consume a <see cref="WindowsConnection.Pending"/> segment
    /// that spans several write pages. False keeps the pre-chunking path, which is always correct.</summary>
    protected virtual bool SupportsOwnedStaging => false;

    /// <summary>Stage ciphertext from the shard's encrypt scratch, taking the cheap path where the backend
    /// supports it. One place, so the two TLS call sites cannot drift apart.</summary>
    protected void StageCipher(TConn conn)
    {
        if (SupportsOwnedStaging)
        {
            var (buf, len) = _tlsCipher!.TakeArray(); // scratch re-rents lazily on next use
            StageOutboundOwned(conn, buf, len);
        }
        else
        {
            StageOutbound(conn, _tlsCipher!.WrittenSpan);
        }
    }

    /// <summary>Drop every queued segment AND return its buffer. Clearing the queue alone leaks the pooled
    /// arrays back to the GC rather than the pool — harmless-looking, and worse now that one segment can be
    /// a whole response rather than a single page.</summary>
    protected static void DiscardPending(TConn conn)
    {
        if (conn.Pending is { Count: > 0 } q)
            while (q.Count > 0) ArrayPool<byte>.Shared.Return(q.Dequeue().Array!);
        conn.Pending?.Clear();
        conn.PendingHeadOffset = 0;
    }

    /// <summary>Deliver a flushed out-of-band write on the loop thread: encrypt if TLS, stage into
    /// Pending, then kick a send if idle. Dropped if the slot was re-tenanted or its send half is
    /// closed.</summary>
    protected void PumpFlush(uint slot, uint generation, byte[] data, int len)
    {
        var conn = _conns[slot - 1];
        if (conn.Generation != generation || conn.Socket == 0 || conn.Closing
            || (conn.Flags & SocketSet.SocketFlags.SendClosed) != 0)
            return;

        if (conn.Tls is { } tls)
        {
            // Out-of-band writes are application plaintext; encrypt before staging. A flush cannot legally
            // arrive before the handshake completes (the app has no Connection reference until the deferred
            // open), so dropping one is strictly safer than letting plaintext reach the wire.
            if (!tls.HandshakeComplete)
            {
                System.Diagnostics.Debug.WriteLine("TLS flush before handshake completion; dropped.");
                return;
            }
            _tlsCipher!.Reset();
            tls.ProcessOutbound(new ReadOnlySpan<byte>(data, 0, len), _tlsCipher);
            StageCipher(conn);
        }
        else
        {
            StageOutbound(conn, new ReadOnlySpan<byte>(data, 0, len));
        }
        if (!conn.SendBusy) StartPendingSend(conn, slot);
    }

    // =====================================================================
    // TLS interception (see TlsFilter)
    // ---------------------------------------------------------------------
    // Both backends have ONE loop thread per shard and every filter call below runs on it, so — unlike the
    // managed fallback, which needs a per-connection gate — there is no locking here and the scratch
    // writers are shared shard-wide.
    //
    // The integration point is the existing Pending queue: ALL ciphertext (handshake flights, control
    // records, encrypted application data) is staged there and drained by the normal send machinery. That
    // is what keeps records in the order the engine produced them — TLS records are sequence-numbered, so
    // a path that jumped ahead of anything already queued would corrupt the stream.
    // =====================================================================

    /// <summary>Stage ciphertext and kick a send if the connection is otherwise idle.</summary>
    protected void QueueCipher(TConn conn, uint slot, ReadOnlySpan<byte> cipher)
    {
        if (cipher.IsEmpty) return;
        StageOutbound(conn, cipher);
        if (!conn.SendBusy && !conn.Closing && conn.Socket != 0) StartPendingSend(conn, slot);
    }

    /// <summary>Encrypt application plaintext and queue the resulting records.</summary>
    protected void SendEncrypted(TConn conn, uint slot, byte* plaintext, int len)
    {
        _tlsCipher!.Reset();
        conn.Tls!.ProcessOutbound(new ReadOnlySpan<byte>(plaintext, len), _tlsCipher);
        if (_tlsCipher.WrittenCount == 0) return;
        StageCipher(conn);
        if (!conn.SendBusy && !conn.Closing && conn.Socket != 0) StartPendingSend(conn, slot);
    }

    /// <summary>Advance the handshake with freshly-received bytes. False once the connection is gone.</summary>
    protected bool DriveTlsHandshake(TConn conn, uint slot, ReadOnlySpan<byte> input)
    {
        _tlsCtrl!.Reset();
        var status = conn.Tls!.DriveHandshake(input, conn.Socket, _tlsCtrl);
        QueueCipher(conn, slot, _tlsCtrl.WrittenSpan); // may carry a fatal alert on failure — send it first

        if (status == TlsHandshakeStatus.Faulted) { CloseClient(slot); return false; }
        if (conn.Closing || conn.Socket == 0) return false; // QueueCipher tore it down (pool exhausted)
        if (status == TlsHandshakeStatus.Completed) FireTlsOpen(conn, slot);
        return !conn.Closing && conn.Socket != 0;
    }

    /// <summary>The deferred open: a TLS connection is only surfaced to the application once the
    /// handshake completes, so OnAccept/OnConnect fire here rather than at adoption.</summary>
    protected void FireTlsOpen(TConn conn, uint slot)
    {
        // The write page here is only scratch for the greeting — the ciphertext goes out via Pending, so
        // the page is released again rather than sent.
        bool leased = _writeBuffer.TryLease(out int wi, out byte* wp);
        conn.Opened = true; // app now sees it open → pairs with OnClosed
        int sb;
        if (conn.IsClient)
        {
            var ctx = new SocketSet.ConnectContext(conn, wp, leased ? _writeBufSize : 0);
            Parent.DispatchConnect(ref ctx);
            sb = ctx.SendBytes;
        }
        else
        {
            var ctx = new SocketSet.AcceptContext(conn, wp, leased ? _writeBufSize : 0);
            Parent.DispatchAccept(ref ctx);
            sb = ctx.SendBytes;
        }

        if (!leased) return;
        if (sb > 0 && !conn.Closing && conn.Socket != 0 && (conn.Flags & SocketSet.SocketFlags.SendClosed) == 0)
            SendEncrypted(conn, slot, wp, sb);
        _writeBuffer.Release(wi);
    }

    /// <summary>Decrypt a received segment and surface the plaintext. False once the connection is gone.</summary>
    protected bool DeliverReceiveTls(TConn conn, uint slot, int bytes)
    {
        var tls = conn.Tls!;
        byte* rp = _recvBuffer.Address(conn.RecvBuf);
        var cipherIn = new ReadOnlySpan<byte>(rp, bytes);

        if (!tls.HandshakeComplete)
        {
            if (!DriveTlsHandshake(conn, slot, cipherIn)) return false;
            if (!tls.HandshakeComplete) return true; // still handshaking; keep receiving

            // Just completed. Application data coalesced into the same segment as the peer's final
            // handshake flight is already buffered INSIDE the engine — surface it now with an empty input,
            // or it strands until a next recv that may never come. See TlsFilter.DriveHandshake.
            cipherIn = default;
        }

        _tlsPlain!.Reset();
        _tlsCtrl!.Reset();
        var status = tls.ProcessInbound(cipherIn, TlsContentType.Ciphertext, _tlsPlain, _tlsCtrl);
        QueueCipher(conn, slot, _tlsCtrl.WrittenSpan); // protocol replies (e.g. a TLS 1.3 KeyUpdate ack)

        if (status == TlsInboundStatus.Faulted) { CloseClient(slot); return false; }
        if (conn.Closing || conn.Socket == 0) return false;

        int plainLen = _tlsPlain.WrittenCount;
        if (plainLen > 0)
        {
            byte[] plain = _tlsPlain.Array;
            fixed (byte* pp = plain)
            {
                var ctx = new SocketSet.ReceiveContext(conn, pp, plain.Length, plainLen);
                Parent.DispatchReceive(ref ctx);
                int rb = ctx.ResponseBytes;
                if (rb > 0 && !conn.Closing && conn.Socket != 0
                    && (conn.Flags & SocketSet.SocketFlags.SendClosed) == 0)
                {
                    SendEncrypted(conn, slot, pp, rb);
                }
            }
        }

        if (status == TlsInboundStatus.PeerClosed) { CloseClient(slot); return false; }
        return !conn.Closing;
    }
}
#endif
