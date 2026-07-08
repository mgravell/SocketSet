namespace FastNet;

/// <summary>
/// The operation a completion refers to. Packed into the low bits of a
/// submission's user_data alongside a connection slot index, so a raw
/// kernel completion can be routed back to the right connection and step.
///
/// Design note: there is deliberately no cross-platform IOEngine interface
/// yet. Abstracting over a single implementation produces the wrong
/// abstraction (the previous attempt did). The interface gets extracted once
/// a second real backend — RIO — exists to abstract against. Until then the
/// io_uring loop is concrete and the seams (RIO/SAEA/accept-proxy/TLS) are
/// honest stubs.
/// </summary>
public enum OpType
{
    Accept = 0,
    Recv = 1,
    Send = 2,
    Close = 3,
    Wake = 4,
}
