namespace SocketSets;

/// <summary>
/// A monotonic millisecond clock for deadline arithmetic. Exists because this project multi-targets
/// net472, where <c>Environment.TickCount64</c> does not exist -- and the obvious substitute,
/// <c>Environment.TickCount</c>, is a 32-bit counter that WRAPS every ~24.9 days, which for a deadline
/// comparison means every connection suddenly looks either ancient or brand new once a month.
/// <see cref="System.Diagnostics.Stopwatch"/> is monotonic and 64-bit on both.
/// </summary>
internal static class Clock
{
#if NET
    public static long Millis => Environment.TickCount64;
#else
    private static readonly double MsPerTick = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    public static long Millis => (long)(System.Diagnostics.Stopwatch.GetTimestamp() * MsPerTick);
#endif
}
