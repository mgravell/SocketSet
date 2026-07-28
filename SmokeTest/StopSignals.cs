#if NET
using System.Runtime.InteropServices;
#endif

namespace SmokeTest;

/// <summary>
/// Shutdown signalling for the long-running modes (<c>--http</c> and the soak/churn loops).
///
/// WHY THIS IS NOT JUST <see cref="Console.CancelKeyPress"/>. Measured 2026-07-28, and it had been on
/// record as a suspected transport defect (TODO item 0c: "io_uring does not always exit on SIGINT after
/// sustained load"). It is neither io_uring's nor load-related:
///
/// A shell WITHOUT job control - which means any non-interactive script, i.e. every rig in `bench/` -
/// starts background (<c>&amp;</c>) children with SIGINT and SIGQUIT set to <c>SIG_IGN</c>. That is POSIX
/// behaviour, so that a Ctrl+C at the terminal cannot kill a background job. .NET then honours the
/// inherited disposition and never raises <c>CancelKeyPress</c>, so the process ignores SIGINT outright.
/// Confirmed from the kernel's own view rather than inferred: the hung process reports
/// <c>SigIgn: ...1006</c> (bits 0x2 SIGINT and 0x4 SIGQUIT, on top of the usual 0x1000 SIGPIPE) and
/// <c>SigCgt: ...44f8</c> - SIGINT not caught. The same binary launched from an interactive shell reports
/// <c>SigIgn: ...1000</c> / <c>SigCgt: ...44fe</c> and exits in 250ms. Nothing to do with the transport:
/// the pump threads are <c>IsBackground</c> and are never joined, so they cannot hold the process open.
///
/// <b>SIGINT is not recoverable here, and that is correct.</b> <see cref="PosixSignalRegistration"/> was
/// tried for it and measured: it ALSO declines to catch a signal inherited as <c>SIG_IGN</c> (verified
/// 2026-07-28 - still hung, <c>SigIgn</c> unchanged). That matches the POSIX convention that a program
/// must not catch what its parent chose to ignore, so `SmokeTest --http &amp;` inside a script ignoring
/// Ctrl+C is the SPECIFIED behaviour, not a defect. Use SIGTERM from a harness; every rig already does.
///
/// <b>What the SIGTERM registration buys, and it is the point of this class.</b> SIGTERM's default
/// disposition kills the process outright, so anything printed during shutdown - notably the
/// <c>SS_URING_STATS=1</c> report - was unreachable whenever a rig stopped a server with a bare
/// <c>kill</c>. With a handler it now shuts down cleanly and the report lands: verified by capturing a
/// <c>[uring-stats:shutdown]</c> line from a scripted run, which was previously impossible. TODO item 0c
/// records the workaround this forced ("do not build a measurement that can only be read at shutdown",
/// hence the reporter's 2s timer); that constraint is now lifted, though the timer is still worth keeping
/// for a process that is hard-killed.
/// </summary>
internal static class StopSignals
{
    /// <summary>Set <paramref name="stop"/> on SIGINT/SIGTERM (and Ctrl+C). Dispose to unregister.</summary>
    public static IDisposable Install(ManualResetEventSlim stop)
    {
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
#if NET
        return new Registrations(
        [
            // Cancel = true means "we handled it": do not run the default disposition (terminate), so the
            // caller's own shutdown path runs instead.
            PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx => { ctx.Cancel = true; stop.Set(); }),
            PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; stop.Set(); }),
        ]);
#else
        // netfx: CancelKeyPress only. The inherited-SIG_IGN case is Unix-specific anyway.
        return new Registrations([]);
#endif
    }

    private sealed class Registrations(IDisposable[] items) : IDisposable
    {
        public void Dispose()
        {
            foreach (var d in items)
            {
                try { d.Dispose(); } catch { /* best effort on shutdown */ }
            }
        }
    }
}
