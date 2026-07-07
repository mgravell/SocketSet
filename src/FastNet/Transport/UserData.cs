// OpType
using FastNet.Native; // IoUringCqe

namespace FastNet.Transport;

/// <summary>
/// Encodes and decodes the <see cref="IoUringCqe.user_data"/> field per
/// <em>our</em> packing convention: slot in the low 32 bits, op in the high 32.
/// This lives in the transport layer, not the native P/Invoke surface: it is an
/// application-level reinterpretation of an opaque kernel field, so it stays
/// visibly apart from the ABI mirror (<see cref="IoUringCqe"/>).
/// </summary>
internal static class IoUringCqeExtensions
{
    // Encode side: the submit path stamps this into the SQE's user_data; the
    // decode side below reads it back off the completion.
    public static ulong Pack(OpType op, int slot) => ((ulong)op << 32) | (uint)slot;

    extension(in IoUringCqe cqe)
    {
        public int Slot => (int)(uint)cqe.user_data;
        public OpType Op => (OpType)(cqe.user_data >> 32);
    }
}
