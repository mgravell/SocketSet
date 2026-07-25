#if NETFRAMEWORK
using System.Text;

namespace SmokeTest;

/// <summary>
/// Span-based overloads that .NET has in the box but .NET Framework does not. System.Memory (pulled in
/// by the library's net472 target) gives us <see cref="System.ReadOnlySpan{T}"/> itself, but not the BCL
/// methods that accept one — so the few we need are bridged here via the pointer overloads that
/// .NET Framework does expose.
/// </summary>
internal static class NetFxShims
{
    /// <summary>
    /// <c>Encoding.GetString(ReadOnlySpan&lt;byte&gt;)</c>. Only reachable on .NET Framework: on modern
    /// .NET the real instance method wins overload resolution, so this file compiles away entirely.
    /// </summary>
    public static unsafe string GetString(this Encoding encoding, ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return string.Empty; // a fixed on an empty span yields null, which GetString rejects
        fixed (byte* ptr = bytes)
        {
            return encoding.GetString(ptr, bytes.Length);
        }
    }
}
#endif
