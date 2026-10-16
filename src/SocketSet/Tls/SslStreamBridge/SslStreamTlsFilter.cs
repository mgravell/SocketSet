#if NET10_0_OR_GREATER
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Net.Security;

namespace SocketSets.Tls.SslStreamBridge;

/// <summary>
/// <see cref="TlsFilter"/> implemented over <see cref="SslStream"/> — the ecosystem's standard TLS shape —
/// by bridging the push contract to a pull stream. THIS EXISTS TO BE MEASURED, not deployed: it is the
/// "what does SslStream's model cost inside this transport" reference leg of the TLS engine A/B
/// (bench/run-tls-engine-ab.sh), and the bridge deliberately CHARGES that model's costs where they fall:
///
///  - SslStream pulls from a Stream and runs async; the filter is sync, push, loop-thread-only. So the
///    filter feeds a shuttle Stream (inbound ciphertext queue in, outbound ciphertext buffer out) and the
///    loop thread BLOCKS on a monitor until the SslStream machinery either produces something or parks
///    pulling on an empty queue ("quiescent") — at which point the filter returns NeedMoreData/Ok exactly
///    like the real engines do. That block-until-quiescent is the reshape; it is the thing being measured.
///  - Every byte crosses one extra staging copy in each direction (queue in, buffer out), and handshake /
///    read progress rides thread-pool continuations instead of staying on the loop. Also charged.
///
/// It violates the spirit of the contract's "never blocks" clause by design; the letter is kept — calls
/// always return once the machinery goes quiescent, and a stall faults loudly after a timeout instead of
/// wedging the loop forever. Do not use this provider for anything except comparison runs.
/// </summary>
internal sealed class SslStreamTlsFilter : TlsFilter
{
    // If the pool makes no observable progress for this long mid-call, something is deadlocked or the
    // pool is starved; a named fault beats an invisible wedged loop thread.
    private const int StallTimeoutMs = 30_000;
    private const int ReadBufferBytes = 32 * 1024;

    private readonly SslStreamTlsProvider _owner;
    private readonly Shuttle _shuttle;
    private readonly SslStream _ssl;
    private readonly bool _client;
    private readonly SslClientAuthenticationOptions? _clientOptions;
    private readonly SslServerAuthenticationOptions? _serverOptions;
    private readonly string?[]? _rejectReason; // written by the provider's validation callback mid-handshake

    private Task? _handshake;
    private Task<int>? _pendingRead;
    private byte[] _readBuf = [];
    private bool _faulted, _disposed;

    internal SslStreamTlsFilter(SslStreamTlsProvider owner, bool client,
        SslClientAuthenticationOptions? clientOptions, SslServerAuthenticationOptions? serverOptions,
        string?[]? rejectReason)
    {
        _owner = owner;
        _client = client;
        _clientOptions = clientOptions;
        _serverOptions = serverOptions;
        _rejectReason = rejectReason;
        _shuttle = new Shuttle();
        _ssl = new SslStream(_shuttle, leaveInnerStreamOpen: true);
    }

    // ===================================================================================
    // Handshake
    // ===================================================================================

    public override TlsHandshakeStatus DriveHandshake(
        ReadOnlySpan<byte> input, nint transportHandle, IBufferWriter<byte> output)
    {
        if (_faulted || _disposed) return TlsHandshakeStatus.Faulted;

        try
        {
            if (_handshake is null)
            {
                _handshake = _client
                    ? _ssl.AuthenticateAsClientAsync(_clientOptions!)
                    : _ssl.AuthenticateAsServerAsync(_serverOptions!);
                PulseOnCompletion(_handshake);
            }
            _shuttle.Feed(input);

            while (true)
            {
                _shuttle.DrainOutbound(output);

                if (_handshake.IsCompleted)
                {
                    // One last drain: the final flight may have been written between the drain above and
                    // the completion check (both sides of that race end up here).
                    _shuttle.DrainOutbound(output);
                    if (!_handshake.IsCompletedSuccessfully)
                    {
                        // Our validation callback saying no surfaces as a generic AuthenticationException;
                        // the real reason was stashed by the provider's callback — prefer it.
                        return Fault(_rejectReason?[0] is { } rejected
                            ? "rejected peer: " + rejected
                            : _handshake.Exception?.GetBaseException().Message ?? "handshake failed");
                    }
                    return CompleteHandshake();
                }

                lock (_shuttle.Sync)
                {
                    if (_handshake.IsCompleted) continue;       // completed since the unlocked check
                    if (_shuttle.QuiescentNoLock) return TlsHandshakeStatus.NeedMoreData;
                    if (!Monitor.Wait(_shuttle.Sync, StallTimeoutMs))
                        return Fault("handshake bridge stalled (no thread-pool progress)");
                }
            }
        }
        catch (Exception ex)
        {
            return Fault(ex.Message);
        }
    }

    private TlsHandshakeStatus CompleteHandshake()
    {
        // Trust was already decided inside the handshake (the provider's ValidateRemote runs as the
        // SslStream RemoteCertificateValidationCallback, so a rejection surfaces as a faulted task above —
        // fail-closed comes free with this engine). Here we only harvest the results.
        var alpn = _ssl.NegotiatedApplicationProtocol;
        NegotiatedProtocol = alpn.Protocol.IsEmpty ? null : alpn.ToString();
        if (!_client)
        {
            string? sni = _ssl.TargetHostName;
            RequestedServerName = string.IsNullOrEmpty(sni) ? null : sni;
        }
        HandshakeComplete = true;
        return TlsHandshakeStatus.Completed;
    }

    // ===================================================================================
    // Data phase
    // ===================================================================================

    public override TlsInboundStatus ProcessInbound(
        ReadOnlySpan<byte> input, TlsContentType contentType,
        IBufferWriter<byte> plaintext, IBufferWriter<byte> output)
    {
        if (_faulted || _disposed) return TlsInboundStatus.Faulted;
        _shuttle.Feed(input);

        try
        {
            while (true)
            {
                _shuttle.DrainOutbound(output); // control-record replies (KeyUpdate acks etc.)

                if (_pendingRead is null)
                {
                    if (_readBuf.Length == 0) _readBuf = ArrayPool<byte>.Shared.Rent(ReadBufferBytes);
                    // The read persists ACROSS calls: when it parks on an empty shuttle we return Ok, and
                    // the next ProcessInbound's Feed resumes it where it blocked.
                    _pendingRead = _ssl.ReadAsync(_readBuf).AsTask();
                    PulseOnCompletion(_pendingRead);
                }

                if (_pendingRead.IsCompleted)
                {
                    if (!_pendingRead.IsCompletedSuccessfully)
                    {
                        Fault(_pendingRead.Exception?.GetBaseException().Message ?? "read failed");
                        return TlsInboundStatus.Faulted;
                    }
                    int n = _pendingRead.Result;
                    _pendingRead = null;
                    if (n == 0)
                    {
                        // SslStream reports the peer's close_notify (or EOF) as a zero-length read.
                        _shuttle.DrainOutbound(output);
                        return TlsInboundStatus.PeerClosed;
                    }
                    var dest = plaintext.GetSpan(n);
                    _readBuf.AsSpan(0, n).CopyTo(dest);
                    plaintext.Advance(n);
                    continue; // SslStream may hold more buffered plaintext; read again until it parks
                }

                lock (_shuttle.Sync)
                {
                    if (_pendingRead.IsCompleted) continue;
                    if (_shuttle.QuiescentNoLock) return TlsInboundStatus.Ok;
                    if (!Monitor.Wait(_shuttle.Sync, StallTimeoutMs))
                    {
                        Fault("inbound bridge stalled (no thread-pool progress)");
                        return TlsInboundStatus.Faulted;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Fault(ex.Message);
            return TlsInboundStatus.Faulted;
        }
    }

    public override void ProcessOutbound(ReadOnlySpan<byte> plaintext, IBufferWriter<byte> output)
    {
        if (_faulted || _disposed || !HandshakeComplete || plaintext.IsEmpty) return;
        try
        {
            // The sync write path: encrypt, then a synchronous inner-stream write — which is the shuttle's
            // append-and-return. No pool hop on this half; the outbound cost is the staging copy.
            _ssl.Write(plaintext);
            _shuttle.DrainOutbound(output);
        }
        catch (Exception ex)
        {
            Fault(ex.Message);
        }
    }

    public override void Shutdown(IBufferWriter<byte> output)
    {
        if (_faulted || _disposed || !HandshakeComplete) return;
        try
        {
            // Writes close_notify to the shuttle; shuttle writes never block, so this completes promptly.
            var t = _ssl.ShutdownAsync();
            PulseOnCompletion(t);
            lock (_shuttle.Sync)
            {
                while (!t.IsCompleted)
                {
                    if (!Monitor.Wait(_shuttle.Sync, 1000)) break; // best-effort, like the other engines
                }
            }
            _shuttle.DrainOutbound(output);
        }
        catch (Exception ex)
        {
            Fault(ex.Message);
        }
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shuttle.CloseInbound(); // unparks any pending read with EOF so the SslStream machinery can unwind
        try { _ssl.Dispose(); } catch { /* a faulted stream may throw from Dispose; teardown must not */ }
        _shuttle.Dispose();
        if (_readBuf.Length != 0)
        {
            PooledBuffers.ReturnCleared(_readBuf, _readBuf.Length); // held PLAINTEXT; clear it all
            _readBuf = [];
        }
    }

    // ===================================================================================
    // Plumbing
    // ===================================================================================

    private void PulseOnCompletion(Task task)
        => task.ContinueWith(
            static (_, state) => { lock (state!) Monitor.PulseAll(state); },
            _shuttle.Sync, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private TlsHandshakeStatus Fault(string reason)
    {
        _faulted = true;
        FaultReason ??= reason;
        Debug.WriteLine($"[SslStreamTlsFilter] {reason}");
        return TlsHandshakeStatus.Faulted;
    }

    /// <summary>
    /// The <see cref="Stream"/> the <see cref="SslStream"/> sits on: reads pull ciphertext the loop thread
    /// fed; writes append ciphertext the loop thread drains. One lock (<see cref="Sync"/>) guards all
    /// state, and every transition pulses it — that monitor is the whole synchronization story between the
    /// loop thread and the SslStream's pool continuations.
    /// </summary>
    private sealed class Shuttle : Stream
    {
        private const int InitialBuffer = 17 * 1024; // one max-size TLS record plus framing

        private byte[] _in = ArrayPool<byte>.Shared.Rent(InitialBuffer);
        private int _inStart, _inLen, _inHigh;
        private byte[] _out = ArrayPool<byte>.Shared.Rent(InitialBuffer);
        private int _outLen, _outHigh;
        private TaskCompletionSource<int>? _parked;
        private Memory<byte> _parkedDest;
        private bool _closed;

        public object Sync { get; } = new();

        /// <summary>True when the machinery can make no further progress without new input: a read is
        /// parked on an empty queue and no outbound bytes are waiting. Caller must hold <see cref="Sync"/>.</summary>
        public bool QuiescentNoLock => _parked is not null && _inLen == 0 && _outLen == 0;

        /// <summary>Loop thread: queue inbound ciphertext, resuming a parked read directly.</summary>
        public void Feed(ReadOnlySpan<byte> data)
        {
            if (data.IsEmpty) return;
            lock (Sync)
            {
                if (_parked is not null && _inLen == 0)
                {
                    // Hand the front of this feed straight to the parked reader, no staging for that part.
                    int n = Math.Min(data.Length, _parkedDest.Length);
                    data[..n].CopyTo(_parkedDest.Span);
                    if (n < data.Length) Append(data[n..]);
                    var tcs = _parked;
                    _parked = null;
                    _parkedDest = default;
                    tcs.TrySetResult(n);
                }
                else
                {
                    Append(data);
                }
                Monitor.PulseAll(Sync);
            }
        }

        /// <summary>Loop thread: move accumulated outbound ciphertext to the caller's writer.</summary>
        public long DrainOutbound(IBufferWriter<byte> output)
        {
            lock (Sync)
            {
                int n = _outLen;
                if (n == 0) return 0;
                var dest = output.GetSpan(n);
                _out.AsSpan(0, n).CopyTo(dest);
                output.Advance(n);
                _outLen = 0;
                Monitor.PulseAll(Sync);
                return n;
            }
        }

        /// <summary>Unpark any pending read with EOF so Dispose can unwind the SslStream machinery.
        /// (Named to not collide with <see cref="Stream.Close"/>, which routes to Dispose.)</summary>
        public void CloseInbound()
        {
            TaskCompletionSource<int>? tcs;
            lock (Sync)
            {
                _closed = true;
                tcs = _parked;
                _parked = null;
                _parkedDest = default;
                Monitor.PulseAll(Sync);
            }
            tcs?.TrySetResult(0);
        }

        private void Append(ReadOnlySpan<byte> data)
        {
            int need = _inLen + data.Length;
            if (_in.Length < need)
            {
                byte[] bigger = ArrayPool<byte>.Shared.Rent(Math.Max(need, _in.Length * 2));
                _in.AsSpan(_inStart, _inLen).CopyTo(bigger);
                PooledBuffers.ReturnCleared(_in, _inHigh);
                _in = bigger;
                _inStart = 0;
                _inHigh = _inLen;
            }
            else if (_inStart > 0 && _inStart + need > _in.Length)
            {
                _in.AsSpan(_inStart, _inLen).CopyTo(_in);
                _inStart = 0;
            }
            data.CopyTo(_in.AsSpan(_inStart + _inLen));
            _inLen = need;
            if (_inStart + need > _inHigh) _inHigh = _inStart + need;
        }

        // ----- the Stream surface SslStream actually uses -----

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            lock (Sync)
            {
                if (_inLen > 0)
                {
                    int n = Math.Min(buffer.Length, _inLen);
                    _in.AsSpan(_inStart, n).CopyTo(buffer.Span);
                    _inLen -= n;
                    _inStart = _inLen == 0 ? 0 : _inStart + n;
                    return new ValueTask<int>(n);
                }
                if (_closed) return new ValueTask<int>(0);
                // Park: RunContinuationsAsynchronously so completing it from Feed (loop thread, holding
                // Sync) cannot run SslStream continuations inline under our lock.
                _parked = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                _parkedDest = buffer;
                Monitor.PulseAll(Sync); // the pump watches for exactly this transition
                return new ValueTask<int>(_parked.Task);
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException("sync Read would deadlock the bridge; SslStream is driven async for reads");

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.IsEmpty) return;
            lock (Sync)
            {
                int need = _outLen + buffer.Length;
                if (_out.Length < need)
                {
                    byte[] bigger = ArrayPool<byte>.Shared.Rent(Math.Max(need, _out.Length * 2));
                    _out.AsSpan(0, _outLen).CopyTo(bigger);
                    PooledBuffers.ReturnCleared(_out, _outHigh);
                    _out = bigger;
                    _outHigh = _outLen;
                }
                buffer.CopyTo(_out.AsSpan(_outLen));
                _outLen = need;
                if (need > _outHigh) _outHigh = need;
                Monitor.PulseAll(Sync);
            }
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Write(buffer.Span);
            return default;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Write(buffer.AsSpan(offset, count));
            return Task.CompletedTask;
        }

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                lock (Sync)
                {
                    // Both buffers carried ciphertext only, but hygiene is cheap and uniform here.
                    PooledBuffers.ReturnCleared(_in, _inHigh);
                    PooledBuffers.ReturnCleared(_out, _outHigh);
                    _in = [];
                    _out = [];
                    _inStart = _inLen = _outLen = 0;
                }
            }
            base.Dispose(disposing);
        }
    }
}
#endif
