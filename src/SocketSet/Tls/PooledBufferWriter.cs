using System.Buffers;

namespace SocketSets.Tls;

/// <summary>
/// A minimal <see cref="IBufferWriter{Byte}"/> backed by <see cref="ArrayPool{Byte}"/>, used to collect a
/// <see cref="TlsFilter"/>'s output (decrypted plaintext, or ciphertext to send). Two usage shapes:
///  - reusable scratch: write, read <see cref="WrittenSpan"/>, <see cref="Reset"/>, repeat (e.g. the
///    per-connection decrypt target);
///  - hand-off: write, then <see cref="TakeArray"/> to transfer buffer ownership to an async send that
///    returns it to the pool itself (e.g. encrypted bytes going to the socket).
/// Not thread-safe; lives on the owning shard's loop thread like the filter it serves.
/// </summary>
internal sealed class PooledBufferWriter : IBufferWriter<byte>, IDisposable
{
    private byte[]? _buf;
    private int _pos;

    /// <summary>Largest capacity this writer has ever held, so a re-rent after <see cref="TakeArray"/>
    /// starts there instead of at the size hint.
    ///
    /// Without this, hand-off mode is a PESSIMISATION on a hot path and measurably so: TakeArray leaves
    /// the writer with no buffer, so the next response re-rents at the first size hint (one TLS record's
    /// worth) and then grows by doubling — and every doubling in <see cref="Ensure"/> is a
    /// <see cref="Buffer.BlockCopy"/> of everything written so far. Encrypting a 256 KB response that way
    /// costs ~4 extra full copies of the ciphertext, which is MORE than the one copy hand-off was
    /// introduced to remove. Measured 2026-08-01: with the naive version the whole optimisation read as a
    /// null result.</summary>
    private int _capacity;

    public PooledBufferWriter(int initialCapacity = 1024)
    {
        _buf = ArrayPool<byte>.Shared.Rent(Math.Max(1, initialCapacity));
        _capacity = _buf.Length;
    }

    public int WrittenCount => _pos;
    public ReadOnlySpan<byte> WrittenSpan => _buf is null ? default : _buf.AsSpan(0, _pos);

    /// <summary>The backing array (for building a pointer-based context over the written data). Valid on a
    /// writer that still owns its buffer (i.e. one used as reusable scratch, never <see cref="TakeArray"/>d).
    /// The written data is <c>[0, <see cref="WrittenCount"/>)</c>; the rest is spare capacity.</summary>
    public byte[] Array => _buf!;

    public void Advance(int count) => _pos += count;

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _buf!.AsMemory(_pos);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _buf!.AsSpan(_pos);
    }

    /// <summary>Rewind to empty, keeping the buffer for reuse.</summary>
    public void Reset() => _pos = 0;

    /// <summary>Transfer the backing array + written length to the caller, which now owns it (and must
    /// return it to <see cref="ArrayPool{Byte}"/>). The writer is left empty and must not be used again
    /// without a fresh buffer — intended for one-shot encrypt-then-send.</summary>
    public (byte[] Buffer, int Length) TakeArray()
    {
        var b = _buf ?? ArrayPool<byte>.Shared.Rent(1);
        int l = _pos;
        _buf = null;
        _pos = 0;
        return (b, l);
    }

    private void Ensure(int sizeHint)
    {
        if (sizeHint < 1) sizeHint = 1;
        if (_buf is null)
        {
            // Re-rent at the high-water mark, not at the hint: this writer is reused for the same shape of
            // work every time, so starting small guarantees the doubling loop below runs on every single
            // response. See _capacity.
            _buf = ArrayPool<byte>.Shared.Rent(Math.Max(sizeHint, _capacity));
            _capacity = _buf.Length;
            _pos = 0;
            return;
        }
        if (_buf.Length - _pos >= sizeHint) return;

        int size = Math.Max(_buf.Length * 2, _pos + sizeHint);
        var bigger = ArrayPool<byte>.Shared.Rent(size);
        Buffer.BlockCopy(_buf, 0, bigger, 0, _pos);
        ArrayPool<byte>.Shared.Return(_buf);
        _buf = bigger;
        _capacity = bigger.Length;
    }

    public void Dispose()
    {
        if (_buf is not null) { ArrayPool<byte>.Shared.Return(_buf); _buf = null; }
    }
}
