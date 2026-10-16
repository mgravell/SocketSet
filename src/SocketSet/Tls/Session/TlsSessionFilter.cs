#if NET11_0_OR_GREATER
#pragma warning disable SYSLIB5007 // TlsBufferSession et al are [Experimental] in .NET 11 — the whole point of this provider
using System.Buffers;
using System.Diagnostics;
using System.Net.Security;

namespace SocketSets.Tls.Session;

/// <summary>
/// The in-box .NET 11 TLS engine (<see cref="TlsBufferSession"/>) shaped as a push filter — the third
/// sibling of <see cref="OpenSsl.OpenSslTlsFilter"/> and <see cref="SChannel.SChannelTlsFilter"/>, and the
/// only one with no native interop of our own: the runtime's PAL does the crypto (OpenSSL on Linux,
/// SChannel on Windows, Network.framework on macOS), while buffers, scheduling and threading stay ours.
/// That makes it the natural engine for the MANAGED backend, which is otherwise the one backend whose TLS
/// rides a provider written for somebody else's platform story — and an A/B counterpart for the two
/// hand-rolled providers everywhere else.
///
/// Semantics that shape this class (from the .NET 11 RC1 source, TlsSession.cs):
///  - The session does NOT buffer partial input: a split record returns NeedMoreData with bytesConsumed=0
///    and the bytes must be re-presented — so this class owns a carry buffer, exactly like the SChannel
///    filter (whose SSPI has the same property), honouring the <see cref="TlsFilter"/> contract that every
///    method consumes all of its input.
///  - Output it could not fit in the destination is stashed INTERNALLY (<see cref="TlsSession.HasPendingOutput"/>)
///    and, while any is owed, new input is ignored — so every loop here drains before it feeds.
///  - <c>Write</c> encrypts at most one record (~16KB plaintext) per call; loop.
///  - Certificate validation is EXTERNAL: the engine pauses with NeedsCertificateValidation and we answer
///    via <see cref="TlsSession.SetRemoteCertificateValidationResult"/>. Validation policy itself lives on
///    the provider (<see cref="TlsSessionProvider.ValidateRemote"/>), matching the SChannel provider. The
///    options also carry an accept-everything callback so the engine can never pre-empt that decision
///    internally — and because "the pause happens" is PAL behaviour we do not control, completion runs a
///    FAIL-CLOSED post-hoc check: a client handshake that completes without our validation having run gets
///    validated (and, on rejection, refused) before <see cref="TlsFilter.HandshakeComplete"/> is ever set.
///
/// Userspace transform only, both directions: the session API has no kTLS seam (the runtime may grow one;
/// today <see cref="TlsCryptoMode.Transform"/> is permanent here, like SChannel).
///
/// Threading: NOT thread-safe, per the base contract — all calls on the owning shard's loop thread.
/// </summary>
internal sealed class TlsSessionFilter : TlsFilter
{
    private const int InitialCarry = 17 * 1024;        // one max-size TLS record (16KB payload) plus framing
    private const int HandshakeOutputHint = 8 * 1024;  // a typical handshake flight; GetSpan may hand back more
    private const int RecordOutputHint = 17 * 1024;    // ciphertext/plaintext for one whole record
    private const int MaxZeroProgressSpins = 8;        // belt-and-braces against an engine state we misread

    private readonly TlsSessionProvider _owner;
    private readonly bool _client;
    private readonly TlsContext _context;
    private readonly bool _ownsContext;
    private readonly string? _verifyName; // validated against the certificate; null == "*", check skipped
    private readonly TlsBufferSession _session;

    private bool _faulted;
    private bool _disposed;
    private bool _shutdownSent;
    private bool _validated; // our ValidateRemote ran (via the engine's pause); gates the post-hoc check

    // Received-but-not-yet-consumed transport bytes; see the class remarks for why this exists. Unlike the
    // SChannel filter's carry this only ever holds CIPHERTEXT (decryption is never in place here), but it
    // is cleared on return anyway: one rule for every TLS carry buffer beats a per-engine argument about
    // which ones are sensitive.
    private byte[] _carry;
    private int _carryStart; // consume cursor: Read takes whole records from the front, and a memmove per
                             // record went quadratic on large receives (16 records per 256KB chunk measured
                             // ~4x off openssl, 2026-10-16); compaction now happens at most once per Append
    private int _carryLen;   // live bytes starting at _carryStart
    private int _highWater;  // furthest byte ever written (so clear-on-return costs what was used)

    internal TlsSessionFilter(TlsSessionProvider owner, bool client, TlsContext context, bool ownsContext, string? verifyName)
    {
        _owner = owner;
        _client = client;
        _context = context;
        _ownsContext = ownsContext;
        _verifyName = verifyName;
        _carry = ArrayPool<byte>.Shared.Rent(InitialCarry);
        _session = new TlsBufferSession();
        try
        {
            _session.SetContext(context);
        }
        catch
        {
            _session.Dispose();
            if (ownsContext) context.Dispose();
            throw;
        }
    }

    // ===================================================================================
    // Handshake
    // ===================================================================================

    public override TlsHandshakeStatus DriveHandshake(
        ReadOnlySpan<byte> input, nint transportHandle, IBufferWriter<byte> output)
    {
        // transportHandle is deliberately unused: it exists so a filter can hand keys to the kernel at
        // handshake completion, and this engine has no way to take them.
        if (_faulted || _disposed) return TlsHandshakeStatus.Faulted;
        Append(input);

        try
        {
            int spins = 0;
            while (true)
            {
                // Drain-first: while output is owed the session ignores input (bytesConsumed stays 0), so
                // feeding it before draining would just spin.
                long drained = DrainPendingOutput(output);

                var destination = output.GetSpan(HandshakeOutputHint);
                var status = _session.Handshake(Carry, destination, out int consumed, out int written);
                if (written > 0) output.Advance(written);
                Consume(consumed);

                bool progress = drained > 0 || consumed > 0 || written > 0;
                if (progress) spins = 0;
                else if (++spins > MaxZeroProgressSpins) return Fault($"handshake stalled ({status})");

                switch (status)
                {
                    case TlsOperationStatus.Complete:
                        DrainPendingOutput(output); // the final flight can overflow the span above
                        return CompleteHandshake();

                    case TlsOperationStatus.NeedMoreData:
                        // A whole flight can arrive in one segment, leaving records still buffered; keep
                        // stepping while we are making progress on them.
                        if (_carryLen > 0 && progress) continue;
                        return TlsHandshakeStatus.NeedMoreData;

                    case TlsOperationStatus.DestinationTooSmall:
                        continue; // output is stashed internally; the drain at the loop head collects it

                    case TlsOperationStatus.NeedsCertificateValidation:
                    {
                        // The engine pauses so the caller decides trust. It pauses on the SERVER too, even
                        // though we never request a client certificate (observed empirically on the Linux
                        // PAL) — there is nothing to judge there, so accept the absence and move on.
                        if (!_client)
                        {
                            _session.SetRemoteCertificateValidationResult(SslPolicyErrors.None);
                            continue;
                        }
                        // Client: policy is the provider's ValidateRemote (chain + hostname), the same
                        // decision the SChannel provider makes.
                        var remote = _session.GetRemoteCertificate(); // session-owned; not ours to dispose
                        bool ok = _owner.ValidateRemote(remote, _verifyName, out string reason);
                        _session.SetRemoteCertificateValidationResult(
                            ok ? SslPolicyErrors.None : SslPolicyErrors.RemoteCertificateChainErrors);
                        _validated = true;
                        if (!ok) return Reject(reason);
                        continue;
                    }

                    case TlsOperationStatus.CertificateRequested:
                        // The server asked for a client certificate; we never volunteer one (parity with
                        // SCH_CRED_NO_DEFAULT_CREDS / the OpenSSL provider). Resume and let the server
                        // decide whether anonymous is acceptable; the spin guard breaks a refusal loop.
                        continue;

                    default:
                        // Closed (peer quit mid-handshake), NeedsTlsContext (cannot happen: the context is
                        // set in the constructor), and anything a future runtime adds.
                        return Fault($"Handshake: {status}");
                }
            }
        }
        catch (Exception ex)
        {
            // The session API throws managed exceptions (AuthenticationException after a rejection,
            // InvalidOperationException on contract misuse); the shard loop is no place for them.
            return Fault(ex.Message);
        }
    }

    /// <summary>Keys are live. FAIL-CLOSED: if this is a verifying client and the engine never paused for
    /// external validation (a PAL difference we do not control), validate here — the peer is refused before
    /// <see cref="TlsFilter.HandshakeComplete"/> is ever observable.</summary>
    private TlsHandshakeStatus CompleteHandshake()
    {
        if (_client && !_validated)
        {
            var remote = _session.GetRemoteCertificate(); // session-owned; not ours to dispose
            if (!_owner.ValidateRemote(remote, _verifyName, out string reason)) return Reject(reason);
        }

        var alpn = _session.NegotiatedApplicationProtocol;
        if (alpn.Protocol.Length > 0) NegotiatedProtocol = AlpnProtocolList.DecodeId(alpn.Protocol.Span);
        if (!_client) RequestedServerName = _session.TargetHostName; // server: the SNI the client sent

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
        Append(input);

        try
        {
            int spins = 0;
            int destinationHint = RecordOutputHint;
            // At least one Read even with nothing carried: the PAL can hold decrypted plaintext internally
            // (e.g. application data that rode in with the final handshake flight), and the shard's
            // follow-up ProcessInbound(empty) after the handshake exists to surface exactly that.
            while (true)
            {
                long drained = DrainPendingOutput(output);

                var destination = plaintext.GetSpan(destinationHint);
                var status = _session.Read(Carry, destination, out int consumed, out int written);
                if (written > 0) plaintext.Advance(written);
                Consume(consumed);

                bool progress = drained > 0 || consumed > 0 || written > 0;
                if (progress) spins = 0;
                else if (++spins > MaxZeroProgressSpins) { Fault($"inbound stalled ({status})"); return TlsInboundStatus.Faulted; }

                switch (status)
                {
                    case TlsOperationStatus.Complete:
                        if (_carryLen == 0 && !_session.HasPendingOutput)
                        {
                            if (written == 0) return TlsInboundStatus.Ok;
                            continue; // an empty-input probe just yielded data; probe once more
                        }
                        continue; // more records buffered

                    case TlsOperationStatus.NeedMoreData:
                        return TlsInboundStatus.Ok; // partial record; it waits in the carry

                    case TlsOperationStatus.DestinationTooSmall:
                        // Either transport output is owed (the drain at the loop head collects it), or the
                        // plaintext span was genuinely too small — grow it, bounded: a record's plaintext
                        // cannot legitimately need more than 4x a max-size record.
                        if (!_session.HasPendingOutput)
                        {
                            destinationHint *= 2;
                            if (destinationHint > 4 * RecordOutputHint) { Fault("inbound record larger than any legal TLS record"); return TlsInboundStatus.Faulted; }
                        }
                        continue;

                    case TlsOperationStatus.Closed:
                        // The peer sent close_notify. Plaintext surfaced above still counts; any reply
                        // (our close_notify) is pending output.
                        DrainPendingOutput(output);
                        return TlsInboundStatus.PeerClosed;

                    default:
                        // NeedsCertificateValidation / CertificateRequested mid-stream would be TLS 1.2
                        // renegotiation or post-handshake auth, neither of which this provider supports —
                        // loud beats silent here, and the smoke matrix will say so if it is ever hit.
                        Fault($"Read: {status}");
                        return TlsInboundStatus.Faulted;
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
            int spins = 0;
            var rest = plaintext;
            while (!rest.IsEmpty)
            {
                long drained = DrainPendingOutput(output);

                // One record per Write call (the engine caps the chunk itself; 16KB keeps the reservation
                // honest). The +1k headroom covers record framing many times over (TLS 1.3 is ~22 bytes).
                int n = Math.Min(rest.Length, 16 * 1024);
                var destination = output.GetSpan(n + 1024);
                var status = _session.Write(rest[..n], destination, out int consumed, out int written);
                if (written > 0) output.Advance(written);
                rest = rest[consumed..];

                if (drained > 0 || consumed > 0 || written > 0) spins = 0;
                else if (++spins > MaxZeroProgressSpins) { Fault($"outbound stalled ({status})"); return; }

                switch (status)
                {
                    case TlsOperationStatus.Complete:
                        continue;
                    case TlsOperationStatus.DestinationTooSmall:
                        continue; // stashed internally; the drain at the loop head collects it
                    default:
                        Fault($"Write: {status}");
                        return;
                }
            }
        }
        catch (Exception ex)
        {
            Fault(ex.Message);
        }
    }

    public override void Shutdown(IBufferWriter<byte> output)
    {
        if (_faulted || _disposed || !HandshakeComplete || _shutdownSent) return;
        _shutdownSent = true;
        try
        {
            var destination = output.GetSpan(256);
            if (_session.Shutdown(destination, out int written) == TlsOperationStatus.Complete && written > 0)
            {
                output.Advance(written);
            }
            DrainPendingOutput(output);
        }
        catch
        {
            // best-effort, exactly like the OpenSSL and SChannel paths
        }
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.Dispose();
        if (_ownsContext) _context.Dispose();
        if (_carry.Length != 0)
        {
            PooledBuffers.ReturnCleared(_carry, _highWater);
            _carry = [];
            _carryStart = 0;
            _carryLen = 0;
            _highWater = 0;
        }
    }

    // ===================================================================================
    // Plumbing
    // ===================================================================================

    private ReadOnlySpan<byte> Carry => _carry.AsSpan(_carryStart, _carryLen);

    /// <summary>Collect whatever the session stashed because an earlier destination was too small. Returns
    /// the number of bytes moved, so callers can count it as progress.</summary>
    private long DrainPendingOutput(IBufferWriter<byte> output)
    {
        long total = 0;
        while (_session.HasPendingOutput)
        {
            var destination = output.GetSpan(RecordOutputHint);
            var status = _session.DrainPendingOutput(destination, out int written);
            if (written > 0)
            {
                output.Advance(written);
                total += written;
            }
            if (status == TlsOperationStatus.Complete) break;
            if (status != TlsOperationStatus.DestinationTooSmall || written == 0)
            {
                Fault($"DrainPendingOutput: {status}");
                break;
            }
        }
        return total;
    }

    private void Append(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        int need = _carryLen + data.Length;
        if (_carry.Length < need)
        {
            byte[] bigger = ArrayPool<byte>.Shared.Rent(Math.Max(need, _carry.Length * 2));
            Carry.CopyTo(bigger);
            PooledBuffers.ReturnCleared(_carry, _highWater);
            _carry = bigger;
            _carryStart = 0;
            _highWater = _carryLen;
        }
        else if (_carryStart > 0 && _carryStart + need > _carry.Length)
        {
            // The live tail fits the buffer but not at the cursor: compact once, here, rather than on
            // every Consume — this is the whole point of the cursor.
            Carry.CopyTo(_carry);
            _carryStart = 0;
        }
        data.CopyTo(_carry.AsSpan(_carryStart + _carryLen));
        _carryLen = need;
        if (_carryStart + need > _highWater) _highWater = _carryStart + need;
    }

    /// <summary>The session consumes from the FRONT (bytesConsumed): advance the cursor and copy nothing.
    /// The tail is only ever moved by <see cref="Append"/>, at most once per receive.</summary>
    private void Consume(int consumed)
    {
        if (consumed <= 0) return;
        if (consumed >= _carryLen) { _carryLen = 0; _carryStart = 0; return; }
        _carryLen -= consumed;
        _carryStart += consumed;
    }

    private TlsHandshakeStatus Fault(string reason)
    {
        _faulted = true;
        FaultReason ??= reason;
        Debug.WriteLine($"[TlsSessionFilter] {reason}");
        return TlsHandshakeStatus.Faulted;
    }

    /// <summary>Fault with a reason of our own (a rejected certificate) rather than an engine status.</summary>
    private TlsHandshakeStatus Reject(string reason)
    {
        _faulted = true;
        FaultReason ??= "rejected peer: " + reason;
        Debug.WriteLine($"[TlsSessionFilter] rejected peer: {reason}");
        return TlsHandshakeStatus.Faulted;
    }
}
#endif
