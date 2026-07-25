using System.Buffers;

namespace SocketSets.Tls;

/// <summary>
/// A NO-CRYPTO <see cref="TlsFilter"/> used to prove the wiring end-to-end before any real TLS engine
/// exists. It runs a trivial one-round-trip "handshake" (so the handshake-gating path is genuinely
/// exercised, not skipped) and then passes application bytes through UNCHANGED in both directions.
///
/// Handshake (1-RTT, so client-speaks-first, server-waits, and both complete on a receive are all
/// exercised): client emits a 1-byte ClientHello marker and waits; server waits for it, replies with a
/// 1-byte marker, and completes; client completes on that reply. After that, <see cref="ProcessInbound"/>
/// / <see cref="ProcessOutbound"/> are pure memcpy, so a byte-exact echo test round-trips unchanged iff
/// the shard's decrypt-in / encrypt-out plumbing is correct. NOT for any real use — there is no security
/// here whatsoever.
/// </summary>
internal sealed class IdentityTlsFilter(bool client) : TlsFilter
{
    private const byte ClientMarker = 0xC1;
    private const byte ServerMarker = 0x51;

    private bool _helloSent; // client: has the ClientHello marker been emitted yet

    public override TlsHandshakeStatus DriveHandshake(
        ReadOnlySpan<byte> input, nint transportHandle, IBufferWriter<byte> output)
    {
        if (client)
        {
            if (!_helloSent)
            {
                output.GetSpan(1)[0] = ClientMarker;
                output.Advance(1);
                _helloSent = true;
                return TlsHandshakeStatus.NeedMoreData; // now await the server's marker
            }
            if (input.IsEmpty) return TlsHandshakeStatus.NeedMoreData;
            HandshakeComplete = true; // saw the server's reply
            return TlsHandshakeStatus.Completed;
        }

        // server: wait for the ClientHello marker, then reply and complete
        if (input.IsEmpty) return TlsHandshakeStatus.NeedMoreData;
        output.GetSpan(1)[0] = ServerMarker;
        output.Advance(1);
        HandshakeComplete = true;
        return TlsHandshakeStatus.Completed;
    }

    public override TlsInboundStatus ProcessInbound(
        ReadOnlySpan<byte> input, TlsContentType contentType,
        IBufferWriter<byte> plaintext, IBufferWriter<byte> output)
    {
        if (!input.IsEmpty)
        {
            input.CopyTo(plaintext.GetSpan(input.Length));
            plaintext.Advance(input.Length);
        }
        return TlsInboundStatus.Ok;
    }

    public override void ProcessOutbound(ReadOnlySpan<byte> plaintext, IBufferWriter<byte> output)
    {
        if (!plaintext.IsEmpty)
        {
            plaintext.CopyTo(output.GetSpan(plaintext.Length));
            output.Advance(plaintext.Length);
        }
    }
}

/// <summary>Provider for the no-crypto <see cref="IdentityTlsFilter"/> — wiring/plumbing tests only.</summary>
public sealed class IdentityTlsProvider : TlsProvider
{
    public override TlsFilter CreateClientFilter(TlsClientOptions options) => new IdentityTlsFilter(client: true);
    public override TlsFilter CreateServerFilter(TlsServerOptions options) => new IdentityTlsFilter(client: false);
}
