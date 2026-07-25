#if NET
using System.Buffers;
using static SocketSets.Tls.OpenSsl.NativeOpenSsl;

namespace SocketSets.Tls.OpenSsl;

/// <summary>
/// A real TLS engine (OpenSSL) shaped as a push filter via a pair of memory BIOs — the design's baseline
/// Linux implementation. The <c>SSL*</c> reads ciphertext from <c>_rbio</c> (we write received bytes in)
/// and writes ciphertext to <c>_wbio</c> (we drain bytes to send out); it never touches the socket. That
/// decoupling is exactly what makes OpenSSL fit a push loop: feed <c>_rbio</c>, call SSL_*; drain <c>_wbio</c>.
///
/// This is the userspace-transform path (both directions stay <see cref="TlsCryptoMode.Transform"/>); kTLS
/// offload would layer on later by handing keys to the kernel at handshake completion and flipping the
/// per-direction modes. All calls run on the owning loop thread (or, on the managed backend, under its
/// per-connection gate), matching the "not thread-safe" contract — an <c>SSL*</c> is not thread-safe.
/// </summary>
internal sealed unsafe class OpenSslTlsFilter(nint ssl, nint rbio, nint wbio) : TlsFilter
{
    // rbio/wbio are owned by the SSL after SSL_set_bio, so SSL_free frees them too — we keep the handles
    // only to write received ciphertext in (rbio) and read ciphertext-to-send out (wbio).
    private bool _freed;

    public override TlsHandshakeStatus DriveHandshake(
        ReadOnlySpan<byte> input, nint transportHandle, IBufferWriter<byte> output)
    {
        if (!input.IsEmpty) BioWriteAll(rbio, input);

        int ret = SSL_do_handshake(ssl);
        DrainOut(output); // send whatever the handshake produced (ClientHello, the server flight, …)

        if (ret == 1)
        {
            HandshakeComplete = true;
            return TlsHandshakeStatus.Completed;
        }

        int err = SSL_get_error(ssl, ret);
        if (err is SSL_ERROR_WANT_READ or SSL_ERROR_WANT_WRITE)
            return TlsHandshakeStatus.NeedMoreData; // need more bytes from the peer

        Fault("handshake");
        return TlsHandshakeStatus.Faulted;
    }

    public override TlsInboundStatus ProcessInbound(
        ReadOnlySpan<byte> input, TlsContentType contentType,
        IBufferWriter<byte> plaintext, IBufferWriter<byte> output)
    {
        if (!input.IsEmpty) BioWriteAll(rbio, input);

        // Pull every whole record's worth of plaintext currently decryptable. SSL_read also drives TLS 1.3
        // post-handshake messages (NewSessionTicket / KeyUpdate) internally, which may enqueue bytes to
        // send — so we always drain _wbio afterwards.
        var status = TlsInboundStatus.Ok;
        while (true)
        {
            Span<byte> dst = plaintext.GetSpan(16 * 1024); // a TLS record is <= 16KB of plaintext
            int n;
            fixed (byte* p = dst) n = SSL_read(ssl, p, dst.Length);
            if (n > 0) { plaintext.Advance(n); continue; }

            int err = SSL_get_error(ssl, n);
            if (err is SSL_ERROR_WANT_READ or SSL_ERROR_WANT_WRITE) break; // no more full records buffered
            if (err == SSL_ERROR_ZERO_RETURN) { status = TlsInboundStatus.PeerClosed; break; } // close_notify
            Fault("read");
            status = TlsInboundStatus.Faulted;
            break;
        }

        DrainOut(output);
        return status;
    }

    public override void ProcessOutbound(ReadOnlySpan<byte> plaintext, IBufferWriter<byte> output)
    {
        var p = plaintext;
        while (!p.IsEmpty)
        {
            int n;
            fixed (byte* src = p) n = SSL_write(ssl, src, p.Length);
            if (n > 0) { p = p[n..]; continue; }
            // With a memory BIO, SSL_write doesn't block; a non-positive return here is a real error.
            Fault("write");
            break;
        }
        DrainOut(output);
    }

    public override void Shutdown(IBufferWriter<byte> output)
    {
        if (_freed) return;
        SSL_shutdown(ssl); // emits close_notify into _wbio (best-effort; we don't wait for the peer's)
        DrainOut(output);
    }

    public override void Dispose()
    {
        if (_freed) return;
        _freed = true;
        SSL_free(ssl); // also frees rbio + wbio (ownership transferred by SSL_set_bio)
    }

    // Feed received ciphertext into the SSL's read side. A memory BIO grows to accept it, but loop to be safe.
    private static void BioWriteAll(nint bio, ReadOnlySpan<byte> data)
    {
        fixed (byte* p = data)
        {
            int off = 0;
            while (off < data.Length)
            {
                int w = BIO_write(bio, p + off, data.Length - off);
                if (w <= 0) return; // BIO full/failed — shouldn't happen for a mem BIO; drop the rest
                off += w;
            }
        }
    }

    // Move all ciphertext the SSL has queued to send from _wbio into the output writer.
    private void DrainOut(IBufferWriter<byte> output)
    {
        while (true)
        {
            long pending = BIO_pending(wbio);
            if (pending <= 0) break;
            Span<byte> dst = output.GetSpan((int)Math.Min(pending, 64 * 1024));
            int n;
            fixed (byte* p = dst) n = BIO_read(wbio, p, dst.Length);
            if (n <= 0) break;
            output.Advance(n);
        }
    }

    private static void Fault(string where)
        => System.Diagnostics.Debug.WriteLine($"[OpenSslTlsFilter] {where} failed: {DrainErrors()}");
}
#endif
