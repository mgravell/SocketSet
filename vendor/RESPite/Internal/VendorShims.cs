// Minimal shims for the two RESPite.Internal symbols that the vendored CycleBuffer files reference.
// Everything else CycleBuffer needs (NullLease, MemoryPoolExtensions, the MemoryManager, UntrimmedMemory)
// is defined within the vendored files themselves. Kept tiny on purpose — this is a vendored COPY for
// experimentation (see vendor/RESPite/NOTICE.md), not the real RESPite.Internal.
using System.Diagnostics;

namespace RESPite.Internal;

/// <summary>No-op stand-in for RESPite's debug counters (only two are referenced by CycleBuffer).</summary>
internal static class DebugCounters
{
    [Conditional("RESPITE_DEBUG_COUNTERS")] public static void OnDiscardFull(long count) { }
    [Conditional("RESPITE_DEBUG_COUNTERS")] public static void OnDiscardPartial(long count) { }
}

/// <summary>Constants for the [Experimental] attributes on the vendored public types.</summary>
internal static class Experiments
{
    public const string Respite = "RESPITE001";
    public const string UrlFormat = "https://stackexchange.github.io/StackExchange.Redis/experiments/{0}";
}

/// <summary>RESPite fills recycled memory with garbage in debug builds to catch use-after-recycle; we
/// don't need that in the vendored copy, so these are no-ops (elided unless RESPITE_DEBUG_SCRAMBLE).</summary>
internal static class VendorMemoryExtensions
{
    [Conditional("RESPITE_DEBUG_SCRAMBLE")] public static void DebugScramble(this System.Memory<byte> memory) { }
    [Conditional("RESPITE_DEBUG_SCRAMBLE")] public static void DebugScramble(this System.ReadOnlyMemory<byte> memory) { }
}
