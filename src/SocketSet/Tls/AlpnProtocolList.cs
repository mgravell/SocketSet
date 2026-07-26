using System.Text;

namespace SocketSets.Tls;

/// <summary>
/// ALPN protocol ids in the one wire encoding every TLS stack shares (RFC 7301 §3.1): each id is a single
/// length byte followed by its ASCII bytes, concatenated. <c>["h2", "http/1.1"]</c> becomes
/// <c>02 'h' '2' 08 'h' 't' 't' 'p' '/' '1' '.' '1'</c>.
///
/// OpenSSL consumes exactly these bytes (<c>SSL_set_alpn_protos</c>, and the client/server lists handed to
/// an ALPN-select callback). SChannel wraps the same bytes in a small SEC_APPLICATION_PROTOCOLS header —
/// the header differs, the list does not — so both providers share this encoder.
/// </summary>
internal static class AlpnProtocolList
{
    /// <summary>Longest single protocol id the encoding can express: the length prefix is one byte.</summary>
    public const int MaxProtocolIdLength = 255;

    /// <summary>Encode a preference-ordered id list, or null when there is nothing to negotiate (a null or
    /// empty list means "do not offer ALPN at all", which is different from offering an empty list).</summary>
    /// <exception cref="ArgumentException">An id is empty, over-long, or not ASCII — all of which would
    /// otherwise produce a malformed extension that fails the handshake with a very unhelpful alert.</exception>
    public static byte[]? Encode(IReadOnlyList<string>? protocols)
    {
        if (protocols is null || protocols.Count == 0) return null;

        int total = 0;
        for (int i = 0; i < protocols.Count; i++) total += 1 + Measure(protocols[i]);

        var encoded = new byte[total];
        int offset = 0;
        for (int i = 0; i < protocols.Count; i++)
        {
            string protocol = protocols[i];
            encoded[offset++] = (byte)protocol.Length;
            offset += Encoding.ASCII.GetBytes(protocol, 0, protocol.Length, encoded, offset);
        }
        return encoded;
    }

    /// <summary>Decode a single SELECTED id (not a list — the negotiated result is one bare id).</summary>
    public static string? DecodeId(ReadOnlySpan<byte> id)
#if NET
        => id.IsEmpty ? null : Encoding.ASCII.GetString(id);
#else
        => id.IsEmpty ? null : Encoding.ASCII.GetString(id.ToArray()); // no span overload on netfx
#endif

    private static int Measure(string protocol)
    {
        if (string.IsNullOrEmpty(protocol))
            throw new ArgumentException("An ALPN protocol id cannot be empty.", nameof(protocol));
        if (protocol.Length > MaxProtocolIdLength)
            throw new ArgumentException(
                $"ALPN protocol id '{protocol}' is {protocol.Length} bytes; the length prefix caps ids at {MaxProtocolIdLength}.",
                nameof(protocol));
        foreach (char c in protocol)
        {
            if (c is < (char)0x20 or > (char)0x7E)
                throw new ArgumentException($"ALPN protocol id '{protocol}' must be printable ASCII.", nameof(protocol));
        }
        return protocol.Length; // ASCII: one byte per char, checked above
    }
}
