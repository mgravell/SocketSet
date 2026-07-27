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
            StageOutbound(conn, _tlsCipher.WrittenSpan);
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
        QueueCipher(conn, slot, _tlsCipher.WrittenSpan);
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
            Parent.OnConnect(ref ctx);
            sb = ctx.SendBytes;
        }
        else
        {
            var ctx = new SocketSet.AcceptContext(conn, wp, leased ? _writeBufSize : 0);
            Parent.OnAccept(ref ctx);
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
