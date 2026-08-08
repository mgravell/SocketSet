using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
#if NET
using System.Runtime.InteropServices;
#endif

namespace SocketSets;

/// <summary>
/// A connection endpoint, stored inline and without allocating: 24 bytes, no <c>unsafe</c>, no fixed
/// buffers, so it compiles identically on every target framework.
///
/// WHY NOT JUST HOLD AN <see cref="IPEndPoint"/>. Two per connection, on a pooled connection table, on the
/// accept path — that is two allocations per accept for something most applications never read. This is a
/// value type living in the connection, populated by a couple of stores from bytes the backend already
/// has, and formatted only if somebody asks.
///
/// WHY 24 BYTES AND NOT 128. A raw <c>sockaddr_in</c> is 16, <c>sockaddr_in6</c> is 28, and
/// <c>SOCKADDR_STORAGE</c> is 128 — the last only because <c>sockaddr_un</c> carries a 108-byte path.
/// Storing the NORMALISED form instead (family, port, 16 address bytes, scope id) covers IPv4 and IPv6 in
/// 24 with no padding waste, and AF_UNIX needs none of it: **an accepted Unix-socket peer is almost always
/// unnamed.** A client that never calls <c>bind()</c> has no address at all, so <c>getpeername</c> returns
/// the family alone; the useful UDS name is the LISTENER's, which is per-listener rather than per
/// connection. So AF_UNIX records the family and stops.
/// </summary>
public readonly struct PeerAddress : IEquatable<PeerAddress>
{
    // AF_INET and AF_UNIX agree across platforms; AF_INET6 DOES NOT (23 on Windows, 10 on Linux), and
    // hard-coding either is a silent misparse of a raw sockaddr on the other. Native backends are IPv4-only
    // today (see the 2026-08-04 audit note in Native/LibC.cs), so this matters mainly for the managed
    // backend and for whenever IPv6 lands — which is exactly when a wrong constant would be hardest to find.
    private const ushort AfInet = 2;
    private const ushort AfUnix = 1;
#if NET
    private static readonly ushort AfInet6Native =
        (ushort)(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? 23 : 10);
#else
    private const ushort AfInet6Native = 23; // net472 is Windows-only
#endif

    // Canonical internal family tags, chosen to match System.Net.Sockets.AddressFamily so Family is free.
    private const ushort TagInet = (ushort)AddressFamily.InterNetwork;    // 2
    private const ushort TagInet6 = (ushort)AddressFamily.InterNetworkV6; // 23 in .NET, on every OS
    private const ushort TagUnix = (ushort)AddressFamily.Unix;            // 1

    private readonly ushort _family;  // 0 == unset. Otherwise one of the Tag* values above.
    private readonly ushort _port;    // HOST order. Network order is a wire detail; nobody wants it here.
    private readonly uint _scopeId;   // IPv6 only.
    private readonly ulong _hi;       // address bytes 0-7,  big-endian packed
    private readonly ulong _lo;       // address bytes 8-15, big-endian packed

    private PeerAddress(ushort family, ushort port, uint scopeId, ulong hi, ulong lo)
    {
        _family = family;
        _port = port;
        _scopeId = scopeId;
        _hi = hi;
        _lo = lo;
    }

    /// <summary>False when nothing was recorded — tracking disabled, a backend that cannot supply it, or
    /// an unnamed AF_UNIX peer's address. Callers must handle it; there is no "unknown" string.</summary>
    public bool IsSet => _family != 0;

    /// <summary>The address family, or <see cref="AddressFamily.Unspecified"/> when <see cref="IsSet"/>
    /// is false.</summary>
    public AddressFamily Family => _family == 0 ? AddressFamily.Unspecified : (AddressFamily)_family;

    /// <summary>Port in HOST order, or 0 for families that have none (AF_UNIX has no port at all).</summary>
    public int Port => _port;

    /// <summary>
    /// True when this address is same-host by construction or by value: any AF_UNIX peer (a Unix socket
    /// has no network form), 127.0.0.0/8, or ::1. False when <see cref="IsSet"/> is false — an unknown
    /// address must never read as local, because callers use this for permission decisions.
    /// </summary>
    public bool IsLoopback => _family switch
    {
        TagUnix => true,
        TagInet => (byte)(_hi >> 56) == 127,
        TagInet6 => _hi == 0 && _lo == 1,
        _ => false,
    };

    public static PeerAddress Unix { get; } = new(TagUnix, 0, 0, 0, 0);

    public static PeerAddress FromIPv4(uint addressNetworkOrder, ushort port)
        => new(TagInet, port, 0, (ulong)addressNetworkOrder << 32, 0);

    public static PeerAddress FromIPv6(ReadOnlySpan<byte> address16, ushort port, uint scopeId)
        => address16.Length < 16
            ? default
            : new(TagInet6, port, scopeId,
                  BinaryPrimitives.ReadUInt64BigEndian(address16),
                  BinaryPrimitives.ReadUInt64BigEndian(address16.Slice(8)));

    /// <summary>
    /// Parse a raw kernel <c>sockaddr</c>. Returns <c>default</c> (unset) for a family we do not model,
    /// rather than guessing — a wrong address is worse than no address when the caller may be making a
    /// permission decision with it.
    ///
    /// <paramref name="sockaddr"/> is read in NATIVE byte order for the family field and network order for
    /// port/address, which is what every backend's accept buffer already contains.
    /// </summary>
    public static PeerAddress FromSockAddr(ReadOnlySpan<byte> sockaddr)
    {
        if (sockaddr.Length < 2) return default;
        ushort family = BinaryPrimitives.ReadUInt16LittleEndian(sockaddr); // sa_family is host order

        if (family == AfInet)
        {
            if (sockaddr.Length < 8) return default;
            ushort port = BinaryPrimitives.ReadUInt16BigEndian(sockaddr.Slice(2));
            uint addr = BinaryPrimitives.ReadUInt32BigEndian(sockaddr.Slice(4));
            return FromIPv4(addr, port);
        }
        if (family == AfInet6Native)
        {
            // family 2, port 2, flowinfo 4, addr 16, scope_id 4 == 28
            if (sockaddr.Length < 28) return default;
            ushort port = BinaryPrimitives.ReadUInt16BigEndian(sockaddr.Slice(2));
            uint scope = BinaryPrimitives.ReadUInt32LittleEndian(sockaddr.Slice(24));
            return FromIPv6(sockaddr.Slice(8, 16), port, scope);
        }
        if (family == AfUnix) return Unix; // the path, if any, belongs to the listener - see the type doc

        return default;
    }

    /// <summary>From a managed endpoint — the path the SAEA backend takes, and the only one that can
    /// currently produce IPv6.</summary>
    public static PeerAddress FromEndPoint(EndPoint? endPoint)
    {
        switch (endPoint)
        {
            case IPEndPoint ip when ip.AddressFamily == AddressFamily.InterNetwork:
#pragma warning disable CS0618 // Address is obsolete, but is exactly the 4 bytes we want, without allocating
                return FromIPv4(unchecked((uint)IPAddress.HostToNetworkOrder((int)ip.Address.Address)), (ushort)ip.Port);
#pragma warning restore CS0618
            case IPEndPoint ip6 when ip6.AddressFamily == AddressFamily.InterNetworkV6:
                {
                    Span<byte> b = stackalloc byte[16];
#if NET
                    if (!ip6.Address.TryWriteBytes(b, out int written) || written != 16) return default;
#else
                    var bytes = ip6.Address.GetAddressBytes();
                    if (bytes.Length != 16) return default;
                    bytes.AsSpan().CopyTo(b);
#endif
                    return FromIPv6(b, (ushort)ip6.Port, (uint)ip6.Address.ScopeId);
                }
#if NET
            // UnixDomainSocketEndPoint does not exist on net472, where the managed fallback is the only
            // backend and AF_UNIX is not reachable anyway.
            case UnixDomainSocketEndPoint: return Unix;
#endif
            default: return default;
        }
    }

    /// <summary>Write the address bytes (4 for IPv4, 16 for IPv6) in network order. False if unset or
    /// AF_UNIX, or if <paramref name="destination"/> is too small.</summary>
    public bool TryGetAddressBytes(Span<byte> destination, out int written)
    {
        switch (_family)
        {
            case TagInet when destination.Length >= 4:
                BinaryPrimitives.WriteUInt32BigEndian(destination, (uint)(_hi >> 32));
                written = 4;
                return true;
            case TagInet6 when destination.Length >= 16:
                BinaryPrimitives.WriteUInt64BigEndian(destination, _hi);
                BinaryPrimitives.WriteUInt64BigEndian(destination.Slice(8), _lo);
                written = 16;
                return true;
            default:
                written = 0;
                return false;
        }
    }

    /// <summary>
    /// Format as <c>1.2.3.4:5678</c> / <c>[::1]:5678</c> / <c>unix</c> without allocating. IPv4 is written
    /// by hand; IPv6 defers to <see cref="IPAddress"/>, which allocates — it is the rarer path and only on
    /// demand, and hand-rolling RFC 5952's zero-run compression to save it is not worth the bug surface.
    /// </summary>
    public bool TryFormat(Span<char> destination, out int charsWritten)
    {
        charsWritten = 0;
        switch (_family)
        {
            case 0: return false;
            case TagUnix: return "unix".AsSpan().TryCopyTo(destination) && Set(4, out charsWritten);
            case TagInet:
                {
                    Span<char> tmp = stackalloc char[21]; // 15 for the address + ':' + 5 for the port
                    int n = 0;
                    uint a = (uint)(_hi >> 32);
                    for (int shift = 24; shift >= 0; shift -= 8)
                    {
                        if (shift != 24) tmp[n++] = '.';
                        n += WriteByte(tmp.Slice(n), (byte)(a >> shift));
                    }
                    tmp[n++] = ':';
                    n += WriteUShort(tmp.Slice(n), _port);
                    return tmp.Slice(0, n).TryCopyTo(destination) && Set(n, out charsWritten);
                }
            default:
                {
                    var s = ToString();
                    return s.AsSpan().TryCopyTo(destination) && Set(s.Length, out charsWritten);
                }
        }

        static bool Set(int n, out int written) { written = n; return true; }
    }

    private static int WriteByte(Span<char> d, byte v) => WriteUShort(d, v);

    private static int WriteUShort(Span<char> d, ushort v)
    {
        int digits = v >= 10000 ? 5 : v >= 1000 ? 4 : v >= 100 ? 3 : v >= 10 ? 2 : 1;
        for (int i = digits - 1; i >= 0; i--) { d[i] = (char)('0' + (v % 10)); v /= 10; }
        return digits;
    }

    /// <summary>Allocates. Named plainly so the cost is visible at the call site; prefer
    /// <see cref="TryFormat"/> on any path that runs per connection.</summary>
    public IPEndPoint? ToIPEndPoint()
    {
        switch (_family)
        {
            case TagInet:
                {
                    Span<byte> b = stackalloc byte[4];
                    BinaryPrimitives.WriteUInt32BigEndian(b, (uint)(_hi >> 32));
#if NET
                    return new IPEndPoint(new IPAddress(b), _port);
#else
                    return new IPEndPoint(new IPAddress(b.ToArray()), _port);
#endif
                }
            case TagInet6:
                {
                    Span<byte> b = stackalloc byte[16];
                    BinaryPrimitives.WriteUInt64BigEndian(b, _hi);
                    BinaryPrimitives.WriteUInt64BigEndian(b.Slice(8), _lo);
#if NET
                    return new IPEndPoint(new IPAddress(b, _scopeId), _port);
#else
                    return new IPEndPoint(new IPAddress(b.ToArray(), _scopeId), _port);
#endif
                }
            default: return null;
        }
    }

    public override string ToString()
    {
        switch (_family)
        {
            case 0: return "";
            case TagUnix: return "unix";
            case TagInet:
                {
                    Span<char> buf = stackalloc char[21];
                    return TryFormat(buf, out int n) ? buf.Slice(0, n).ToString() : "";
                }
            default:
                {
                    var ep = ToIPEndPoint();
                    return ep is null ? "" : $"[{ep.Address}]:{ep.Port}";
                }
        }
    }

    public bool Equals(PeerAddress other)
        => _family == other._family && _port == other._port && _scopeId == other._scopeId
           && _hi == other._hi && _lo == other._lo;

    public override bool Equals(object? obj) => obj is PeerAddress other && Equals(other);

    public override int GetHashCode()
        => unchecked((_family * 397) ^ (_port * 31) ^ (int)_scopeId ^ (int)_hi ^ (int)(_hi >> 32) ^ (int)_lo);

    public static bool operator ==(PeerAddress left, PeerAddress right) => left.Equals(right);
    public static bool operator !=(PeerAddress left, PeerAddress right) => !left.Equals(right);
}
