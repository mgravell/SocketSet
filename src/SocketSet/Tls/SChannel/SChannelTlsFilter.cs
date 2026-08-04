#if NET
using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using static SocketSets.Tls.SChannel.NativeSspi;

namespace SocketSets.Tls.SChannel;

/// <summary>
/// A real TLS engine (Windows SChannel, via SSPI) shaped as a push filter — the Windows counterpart to
/// <see cref="OpenSsl.OpenSslTlsFilter"/>, and the reason this backend does not need an
/// <c>SslStream</c>. SSPI is already buffer-in/buffer-out: <see cref="NativeSspi.InitializeSecurityContext"/> /
/// <see cref="NativeSspi.AcceptSecurityContext"/> drive the handshake from byte buffers, and
/// <see cref="NativeSspi.EncryptMessage"/> / <see cref="NativeSspi.DecryptMessage"/> transform records in place. So the whole
/// pull-shaped <c>Stream</c> inversion an <c>SslStream</c> would impose simply does not arise; the shard's
/// existing feed-and-drain loop maps onto SSPI one-for-one.
///
/// Two places where it is genuinely nicer than <c>SslStream</c>:
///  - OUTBOUND is zero-copy framing. <see cref="NativeSspi.EncryptMessage"/> takes header/data/trailer buffers that
///    all point into ONE span, so we reserve <c>cbHeader + n + cbTrailer</c> straight out of the caller's
///    <see cref="IBufferWriter{T}"/> and let SChannel frame the record in place.
///  - INBOUND decrypts in place, handing back the plaintext as a sub-range of the ciphertext buffer.
///
/// And the one place it is worse than an OpenSSL memory BIO, which drives most of the state below:
/// SCHANNEL DOES NOT BUFFER PARTIAL RECORDS. A short read yields SEC_E_INCOMPLETE_MESSAGE and the bytes
/// must be re-presented, prepended, on the next call — so this class owns a <see cref="_carry"/> buffer to
/// honour the <see cref="TlsFilter"/> contract that every method consumes all of its input. The same
/// buffer absorbs SECBUFFER_EXTRA: the unconsumed tail that appears when application data arrives coalesced
/// with the peer's final handshake flight, or when several records land in one segment.
///
/// Userspace transform only, in both directions — Windows has no kTLS equivalent to offload to (there is
/// no <c>setsockopt(SOL_TLS)</c>; the only in-kernel TLS is http.sys terminating HTTPS, which a raw socket
/// cannot reach). <see cref="TlsCryptoMode.Transform"/> is therefore permanent here.
///
/// Threading: NOT thread-safe, per the base contract — an SSPI context must not be used concurrently.
/// </summary>
internal sealed unsafe class SChannelTlsFilter : TlsFilter
{
    private const int InitialCarry = 17 * 1024; // one max-size TLS record (16KB payload) plus framing

    private readonly SChannelTlsProvider _owner;
    private readonly bool _client;
    private readonly string? _sniName;    // sent as SNI; null for "*" and for an IP literal (RFC 6066)
    private readonly string? _verifyName; // validated against the certificate; null == "*", check skipped

    // A ready-made SECBUFFER_APPLICATION_PROTOCOLS payload (header + RFC 7301 list), or null for no ALPN.
    // Built by the provider so it is shaped once per configuration rather than once per handshake step.
    private readonly byte[]? _alpn;

    private SecHandle _ctx;
    private bool _hasCtx;
    private bool _faulted;
    private bool _disposed;
    private bool _shutdownSent;
    private bool _retriedCreds; // SEC_I_INCOMPLETE_CREDENTIALS is retried exactly once

    // Received-but-not-yet-consumed transport bytes. SChannel hands back a count of what it did NOT
    // take (SECBUFFER_EXTRA) rather than buffering it, so this is ours to keep.
    private byte[] _carry;
    private int _carryLen;

    private SecPkgContextStreamSizes _sizes;

    internal SChannelTlsFilter(SChannelTlsProvider owner, bool client, string? sniName, string? verifyName, byte[]? alpn)
    {
        _owner = owner;
        _client = client;
        _sniName = sniName;
        _verifyName = verifyName;
        _alpn = alpn;
        _carry = ArrayPool<byte>.Shared.Rent(InitialCarry);
    }

    // ===================================================================================
    // Handshake
    // ===================================================================================

    public override TlsHandshakeStatus DriveHandshake(
        ReadOnlySpan<byte> input, nint transportHandle, IBufferWriter<byte> output)
    {
        // transportHandle is deliberately unused: it exists so a filter can hand keys to the kernel at
        // handshake completion, and Windows has nowhere to hand them to.
        if (_faulted || _disposed) return TlsHandshakeStatus.Faulted;
        Append(input);

        // A server has nothing to say until it has seen a ClientHello; a client speaks first, from an
        // empty input, so only the server short-circuits here.
        if (!_client && !_hasCtx && _carryLen == 0) return TlsHandshakeStatus.NeedMoreData;

        while (true)
        {
            int before = _carryLen;
            int status = SspiStep(output);
            switch (status)
            {
                case SEC_E_INCOMPLETE_MESSAGE:
                    // Partial record: _carry is untouched, wait for the rest.
                    return TlsHandshakeStatus.NeedMoreData;

                case SEC_I_CONTINUE_NEEDED:
                case SEC_I_COMPLETE_AND_CONTINUE: // SChannel does not use the COMPLETE_* pair; treat as continue
                    // A whole flight can arrive in one segment, leaving records still buffered. Step again
                    // while we are making progress (carry strictly shrinks, so this terminates).
                    if (_carryLen > 0 && _carryLen < before) continue;
                    return TlsHandshakeStatus.NeedMoreData;

                case SEC_I_INCOMPLETE_CREDENTIALS:
                    // The server asked for a client certificate we do not have. The documented response is
                    // to call again unchanged; SChannel then sends an empty certificate list and the server
                    // decides whether that is acceptable.
                    if (_retriedCreds) return Fault("handshake (client certificate required)", status);
                    _retriedCreds = true;
                    continue;

                case SEC_E_OK:
                case SEC_I_COMPLETE_NEEDED:
                    return CompleteHandshake();

                default:
                    // The output token, already emitted above, usually carries the TLS alert explaining this.
                    return Fault(_client ? "InitializeSecurityContext" : "AcceptSecurityContext", status);
            }
        }
    }

    /// <summary>
    /// One InitializeSecurityContext/AcceptSecurityContext round: feed whatever is in <see cref="_carry"/>,
    /// emit any token SChannel produces, and retain the unconsumed tail. Returns the raw SECURITY_STATUS.
    /// Shared by the handshake and the post-handshake (renegotiation / TLS 1.3 key-update) path.
    /// </summary>
    private int SspiStep(IBufferWriter<byte> output)
    {
        SecBuffer* ins = stackalloc SecBuffer[3];
        SecBuffer* outs = stackalloc SecBuffer[1];
        int attrs = 0, status;
        bool hadCarry = _carryLen > 0;

        // ALPN rides on the FIRST call only — the one that produces the ClientHello, or (server) the one
        // that reads it. After a context exists the extension has already been sent or answered.
        bool sendAlpn = !_hasCtx && _alpn is not null;

        fixed (byte* carry = _carry)
        fixed (byte* alpn = _alpn) // null array fixes to a null pointer; only read when sendAlpn
        fixed (SecHandle* ctx = &_ctx)
        fixed (char* host = _sniName) // a null string fixes to a null pointer: no SNI, no target name
        {
            int inCount = 0;
            if (hadCarry)
            {
                ins[inCount++] = new SecBuffer { cbBuffer = _carryLen, BufferType = SECBUFFER_TOKEN, pvBuffer = carry };
                // SECBUFFER_EMPTY; SSPI rewrites this to SECBUFFER_EXTRA/MISSING on return. It must stay at
                // index 1 — the unconsumed-tail check below reads exactly that slot.
                ins[inCount++] = default;
            }
            if (sendAlpn)
            {
                ins[inCount++] = new SecBuffer
                {
                    cbBuffer = _alpn!.Length,
                    BufferType = SECBUFFER_APPLICATION_PROTOCOLS,
                    pvBuffer = alpn,
                };
            }
            var inDesc = new SecBufferDesc { ulVersion = SECBUFFER_VERSION, cBuffers = inCount, pBuffers = ins };

            // ISC_REQ_ALLOCATE_MEMORY: SChannel allocates the output token; we free it with FreeContextBuffer.
            outs[0] = new SecBuffer { BufferType = SECBUFFER_TOKEN };
            var outDesc = new SecBufferDesc { ulVersion = SECBUFFER_VERSION, cBuffers = 1, pBuffers = outs };

            // A client's very first call has no input token at all; it still needs the descriptor when it
            // is carrying the ALPN buffer.
            SecBufferDesc* pIn = inCount > 0 ? &inDesc : null;
            SecHandle* existing = _hasCtx ? ctx : null;

            status = _client
                ? InitializeSecurityContext(_owner.ClientCredential, existing, host,
                    ClientRequestFlags, 0, 0, pIn, 0, ctx, &outDesc, &attrs, null)
                : AcceptSecurityContext(_owner.ServerCredential, existing, pIn,
                    ServerRequestFlags, 0, ctx, &outDesc, &attrs, null);
        }

        // The context handle is only populated on these; in particular a server that received a PARTIAL
        // ClientHello gets SEC_E_INCOMPLETE_MESSAGE and NO context, so latching _hasCtx there would make
        // the next call pass a garbage handle.
        if (status is SEC_E_OK or SEC_I_CONTINUE_NEEDED or SEC_I_COMPLETE_NEEDED
            or SEC_I_COMPLETE_AND_CONTINUE or SEC_I_INCOMPLETE_CREDENTIALS)
        {
            _hasCtx = true;
        }

        if (outs[0].pvBuffer != null)
        {
            if (outs[0].cbBuffer > 0)
                output.Write(new ReadOnlySpan<byte>(outs[0].pvBuffer, outs[0].cbBuffer));
            FreeContextBuffer(outs[0].pvBuffer);
        }

        if (status == SEC_E_INCOMPLETE_MESSAGE) return status; // nothing consumed; keep _carry as-is

        // Whatever SChannel did not consume is always the TAIL of what we handed it. During the handshake
        // only SECBUFFER_EXTRA.cbBuffer is meaningful (pvBuffer is not filled in), so work from the count.
        Compact(hadCarry && ins[1].BufferType == SECBUFFER_EXTRA ? ins[1].cbBuffer : 0);
        return status;
    }

    /// <summary>Keys are live: capture the record geometry and — on a client — decide whether we actually
    /// trust the peer. Any leftover in <see cref="_carry"/> is application data that rode in with the final
    /// handshake flight; the shard's follow-up ProcessInbound(empty) surfaces it.</summary>
    private TlsHandshakeStatus CompleteHandshake()
    {
        if (!RefreshStreamSizes(out int status)) return Fault("QueryContextAttributes(STREAM_SIZES)", status);
        CaptureNegotiatedProtocol();

        if (_client)
        {
            using var remote = GetRemoteCertificate();
            // No alert to send: the handshake itself succeeded — we are rejecting the peer.
            if (!_owner.ValidateRemote(remote, _verifyName, out string reason)) return Reject(reason);
        }

        HandshakeComplete = true;
        return TlsHandshakeStatus.Completed;
    }

    /// <summary>Read back what ALPN settled on. Absence is not an error: the peer may not speak ALPN, and
    /// SChannel then reports status None and we leave <see cref="TlsFilter.NegotiatedProtocol"/> null.
    /// (A client that offered ALPN to a server that supports it but shares no id never reaches here — that
    /// handshake fails with no_application_protocol.)</summary>
    private void CaptureNegotiatedProtocol()
    {
        if (_alpn is null) return;

        SecPkgContextApplicationProtocol result;
        int status;
        fixed (SecHandle* ctx = &_ctx)
        {
            status = QueryContextAttributes(ctx, SECPKG_ATTR_APPLICATION_PROTOCOL, &result);
        }
        if (status != SEC_E_OK
            || result.ProtoNegoStatus != SecApplicationProtocolNegotiationStatus_Success
            || result.ProtocolIdSize == 0)
        {
            return;
        }
        NegotiatedProtocol = AlpnProtocolList.DecodeId(new ReadOnlySpan<byte>(result.ProtocolId, result.ProtocolIdSize));
    }

    private bool RefreshStreamSizes(out int status)
    {
        fixed (SecHandle* ctx = &_ctx)
        fixed (SecPkgContextStreamSizes* sizes = &_sizes)
        {
            status = QueryContextAttributes(ctx, SECPKG_ATTR_STREAM_SIZES, sizes);
        }
        return status == SEC_E_OK;
    }

    /// <summary>The negotiated server certificate, as a managed object. The SSPI cert context is ours to
    /// free; the <see cref="X509Certificate2"/> constructor duplicates it, so both are released.</summary>
    private X509Certificate2? GetRemoteCertificate()
    {
        nint certContext = 0;
        int status;
        fixed (SecHandle* ctx = &_ctx)
        {
            status = QueryContextAttributes(ctx, SECPKG_ATTR_REMOTE_CERT_CONTEXT, &certContext);
        }
        if (status != SEC_E_OK || certContext == 0) return null;
        try { return new X509Certificate2(certContext); }
        catch { return null; }
        finally { CertFreeCertificateContext(certContext); }
    }

    // ===================================================================================
    // Data phase
    // ===================================================================================

    public override TlsInboundStatus ProcessInbound(
        ReadOnlySpan<byte> input, TlsContentType contentType,
        IBufferWriter<byte> plaintext, IBufferWriter<byte> output)
    {
        if (_faulted || _disposed) return TlsInboundStatus.Faulted;
        Append(input);

        SecBuffer* bufs = stackalloc SecBuffer[4];
        var result = TlsInboundStatus.Ok;

        while (_carryLen > 0)
        {
            int status, extra = 0;
            fixed (byte* carry = _carry)
            fixed (SecHandle* ctx = &_ctx)
            {
                bufs[0] = new SecBuffer { cbBuffer = _carryLen, BufferType = SECBUFFER_DATA, pvBuffer = carry };
                bufs[1] = default;
                bufs[2] = default;
                bufs[3] = default;
                var desc = new SecBufferDesc { ulVersion = SECBUFFER_VERSION, cBuffers = 4, pBuffers = bufs };

                status = DecryptMessage(ctx, &desc, 0, null);

                if (status is SEC_E_OK or SEC_I_RENEGOTIATE or SEC_I_CONTEXT_EXPIRED)
                {
                    // The plaintext SecBuffer points INSIDE _carry (decryption is in place), so it has to
                    // be copied out before Compact shuffles the buffer. _carry stays pinned meanwhile.
                    for (int i = 1; i < 4; i++)
                    {
                        if (bufs[i].BufferType == SECBUFFER_DATA && bufs[i].cbBuffer > 0 && bufs[i].pvBuffer != null)
                            plaintext.Write(new ReadOnlySpan<byte>(bufs[i].pvBuffer, bufs[i].cbBuffer));
                        else if (bufs[i].BufferType == SECBUFFER_EXTRA)
                            extra = bufs[i].cbBuffer;
                    }
                }
            }

            switch (status)
            {
                case SEC_E_INCOMPLETE_MESSAGE:
                    return result; // partial record; keep _carry intact and wait for more

                case SEC_E_OK:
                    Compact(extra);
                    continue;

                case SEC_I_CONTEXT_EXPIRED:
                    // The peer sent close_notify. Any plaintext decrypted above still counts.
                    Compact(extra);
                    return TlsInboundStatus.PeerClosed;

                case SEC_I_RENEGOTIATE:
                    // Under TLS 1.3 this is the ROUTINE path, not an exotic one: NewSessionTicket and
                    // KeyUpdate both surface here. The extra bytes are handshake records — feed them back
                    // through ISC/ASC, sending whatever it produces, then resume decrypting.
                    //
                    // Under TLS 1.2 on the SERVER it is something else entirely: a client-initiated
                    // renegotiation, i.e. the CVE-2011-1473 DoS shape (a client forcing repeated expensive
                    // server handshakes). Refuse it, matching OpenSslTlsProvider's SSL_OP_NO_RENEGOTIATION.
                    // Note the same deliberate asymmetry as there: the CLIENT still accepts server-initiated
                    // renegotiation, which is a legitimate (if legacy) 1.2 pattern with no DoS upside to
                    // refusing. This only becomes reachable at an opt-in TLS 1.2 floor — at the default 1.3
                    // floor there is no renegotiation in the protocol at all.
                    Compact(extra);
                    if (!_client && !NegotiatedTls13())
                    {
                        Fault("client-initiated TLS 1.2 renegotiation refused", SEC_I_RENEGOTIATE);
                        return TlsInboundStatus.Faulted;
                    }
                    if (!PostHandshake(output)) return TlsInboundStatus.Faulted;
                    continue;

                default:
                    Fault("DecryptMessage", status);
                    return TlsInboundStatus.Faulted;
            }
        }

        return result;
    }

    /// <summary>Whether the connection actually negotiated TLS 1.3, which decides whether a
    /// SEC_I_RENEGOTIATE is a routine post-handshake message or a real renegotiation request.
    ///
    /// Queried rather than inferred from the provider's floor, because the two differ: at an opt-in TLS 1.2
    /// floor a connection may still land on 1.3, and treating its NewSessionTicket as an attack would break
    /// the connection. FAILS CLOSED — if the query fails we report "not 1.3", so the caller refuses. This is
    /// a security control, and the cost of being wrong in that direction is one dropped connection against
    /// an unbounded handshake-DoS window.</summary>
    private bool NegotiatedTls13()
    {
        if (!_hasCtx) return false;
        SecPkgContextConnectionInfo info = default;
        int status;
        fixed (SecHandle* ctx = &_ctx)
        {
            status = QueryContextAttributes(ctx, SECPKG_ATTR_CONNECTION_INFO, &info);
        }
        if (status != SEC_E_OK) return false;
        // SP_PROT_TLS1_3 carries BOTH the client and server bit; mask, never compare.
        return (info.dwProtocol & SP_PROT_TLS1_3) != 0;
    }

    /// <summary>Drive the handshake machinery mid-stream (TLS 1.3 post-handshake messages, or a TLS 1.2
    /// renegotiation). Returns false if the connection is now unusable.</summary>
    private bool PostHandshake(IBufferWriter<byte> output)
    {
        while (true)
        {
            int before = _carryLen;
            switch (SspiStep(output))
            {
                case SEC_E_OK:
                    // A key update can change the record geometry, so re-read it rather than assume.
                    if (!RefreshStreamSizes(out int qs)) { Fault("QueryContextAttributes(STREAM_SIZES)", qs); return false; }
                    return true;

                case SEC_I_CONTINUE_NEEDED:
                case SEC_I_COMPLETE_AND_CONTINUE:
                    if (_carryLen > 0 && _carryLen < before) continue;
                    return true; // needs more from the peer; the decrypt loop will exit on its own

                case SEC_E_INCOMPLETE_MESSAGE:
                    return true;

                case var status:
                    Fault("post-handshake", status);
                    return false;
            }
        }
    }

    public override void ProcessOutbound(ReadOnlySpan<byte> plaintext, IBufferWriter<byte> output)
    {
        if (_faulted || _disposed || !HandshakeComplete || plaintext.IsEmpty) return;

        SecBuffer* bufs = stackalloc SecBuffer[4];
        int header = _sizes.cbHeader, trailer = _sizes.cbTrailer, max = _sizes.cbMaximumMessage;
        var rest = plaintext;

        while (!rest.IsEmpty)
        {
            int n = Math.Min(rest.Length, max);

            // Reserve one whole record in the caller's writer and let SChannel frame it in place: no
            // intermediate buffer, no second copy of the payload.
            Span<byte> record = output.GetSpan(header + n + trailer);
            rest[..n].CopyTo(record[header..]);

            int status, written;
            fixed (byte* p = record)
            fixed (SecHandle* ctx = &_ctx)
            {
                bufs[0] = new SecBuffer { cbBuffer = header, BufferType = SECBUFFER_STREAM_HEADER, pvBuffer = p };
                bufs[1] = new SecBuffer { cbBuffer = n, BufferType = SECBUFFER_DATA, pvBuffer = p + header };
                bufs[2] = new SecBuffer { cbBuffer = trailer, BufferType = SECBUFFER_STREAM_TRAILER, pvBuffer = p + header + n };
                bufs[3] = default;
                var desc = new SecBufferDesc { ulVersion = SECBUFFER_VERSION, cBuffers = 4, pBuffers = bufs };

                status = EncryptMessage(ctx, 0, &desc, 0);
                // The trailer can come back SHORTER than the reservation (it is a maximum), so the record's
                // real length is the sum of what SChannel reports, not what we asked for.
                written = bufs[0].cbBuffer + bufs[1].cbBuffer + bufs[2].cbBuffer;
            }

            if (status != SEC_E_OK) { Fault("EncryptMessage", status); return; }
            output.Advance(written);
            rest = rest[n..];
        }
    }

    public override void Shutdown(IBufferWriter<byte> output)
    {
        if (_faulted || _disposed || !_hasCtx || _shutdownSent) return;
        _shutdownSent = true;

        // ApplyControlToken only records the intent; the close_notify record is produced by the ISC/ASC
        // call that follows it.
        int token = SCHANNEL_SHUTDOWN;
        var buf = new SecBuffer { cbBuffer = sizeof(int), BufferType = SECBUFFER_TOKEN, pvBuffer = (byte*)&token };
        var desc = new SecBufferDesc { ulVersion = SECBUFFER_VERSION, cBuffers = 1, pBuffers = &buf };

        int status;
        fixed (SecHandle* ctx = &_ctx) status = ApplyControlToken(ctx, &desc);
        if (status != SEC_E_OK) return; // best-effort, exactly like the OpenSSL path

        _carryLen = 0; // the shutdown step must not be handed leftover inbound bytes
        SspiStep(output);
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hasCtx)
        {
            fixed (SecHandle* ctx = &_ctx) DeleteSecurityContext(ctx);
            _hasCtx = false;
        }
        if (_carry.Length != 0)
        {
            ArrayPool<byte>.Shared.Return(_carry);
            _carry = [];
            _carryLen = 0;
        }
    }

    // ===================================================================================
    // Carry buffer — the piece an OpenSSL memory BIO would have given us for free
    // ===================================================================================

    private void Append(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        int need = _carryLen + data.Length;
        if (_carry.Length < need)
        {
            byte[] bigger = ArrayPool<byte>.Shared.Rent(Math.Max(need, _carry.Length * 2));
            _carry.AsSpan(0, _carryLen).CopyTo(bigger);
            ArrayPool<byte>.Shared.Return(_carry);
            _carry = bigger;
        }
        data.CopyTo(_carry.AsSpan(_carryLen));
        _carryLen = need;
    }

    /// <summary>Retain only the trailing <paramref name="tail"/> bytes — what SSPI reported as unconsumed.
    /// Source and destination overlap; Span.CopyTo is a memmove, so that is fine.</summary>
    private void Compact(int tail)
    {
        if (tail <= 0 || tail > _carryLen) { _carryLen = 0; return; }
        if (tail != _carryLen) _carry.AsSpan(_carryLen - tail, tail).CopyTo(_carry);
        _carryLen = tail;
    }

    // ===================================================================================

    private const int ClientRequestFlags =
        ISC_REQ_SEQUENCE_DETECT | ISC_REQ_REPLAY_DETECT | ISC_REQ_CONFIDENTIALITY |
        ISC_REQ_ALLOCATE_MEMORY | ISC_REQ_EXTENDED_ERROR | ISC_REQ_STREAM |
        ISC_REQ_MANUAL_CRED_VALIDATION; // we validate in managed code — see SChannelTlsProvider.ValidateRemote

    private const int ServerRequestFlags =
        ASC_REQ_SEQUENCE_DETECT | ASC_REQ_REPLAY_DETECT | ASC_REQ_CONFIDENTIALITY |
        ASC_REQ_ALLOCATE_MEMORY | ASC_REQ_EXTENDED_ERROR | ASC_REQ_STREAM;

    private TlsHandshakeStatus Fault(string where, int status)
    {
        _faulted = true;
        Debug.WriteLine($"[SChannelTlsFilter] {where} failed: {Describe(status)}");
        return TlsHandshakeStatus.Faulted;
    }

    /// <summary>Fault with a reason of our own (a rejected certificate) rather than an SSPI status.</summary>
    private TlsHandshakeStatus Reject(string reason)
    {
        _faulted = true;
        Debug.WriteLine($"[SChannelTlsFilter] rejected peer: {reason}");
        return TlsHandshakeStatus.Faulted;
    }
}
#endif
