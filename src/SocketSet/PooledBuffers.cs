using System.Buffers;

namespace SocketSets;

/// <summary>
/// Returning a pooled array that held sensitive bytes.
///
/// WHY NOT JUST <c>Return(array, clearArray: true)</c> (REVIEW.md D6, Marc's steer 2026-08-04): that
/// clears the WHOLE array, and these arrays are routinely far larger than the part that was used --
/// <see cref="ArrayPool{T}"/> rounds up to a power of two, and the TLS writers grow to a connection's
/// high-water mark and stay there. Clearing 64KB to retire 40 bytes of RESP is the wrong trade on a path
/// that runs per message. So the used length is passed in and only that much is cleared.
///
/// WHAT THIS IS FOR. These pools are process-wide (<see cref="ArrayPool{T}.Shared"/>), so an array
/// returned holding decrypted plaintext can be handed straight to unrelated code -- including other
/// connections' buffers. Same shape as the receive-buffer tail wipe on the callback contexts, one layer
/// down, and the same limit applies: this defends against accidental retention, not against hostile
/// in-process code, which is already past every boundary the library has.
/// </summary>
internal static class PooledBuffers
{
    /// <summary>Clear the first <paramref name="used"/> bytes, then return the array to the shared pool.
    /// <paramref name="used"/> is clamped, so an over-count is safe rather than an exception on a
    /// teardown path.</summary>
    public static void ReturnCleared(byte[]? array, int used)
    {
        if (array is null) return;
        int n = used < 0 ? 0 : (used > array.Length ? array.Length : used);
        if (n > 0) array.AsSpan(0, n).Clear();
        ArrayPool<byte>.Shared.Return(array);
    }

    /// <summary>Clear and return an <see cref="ArraySegment{T}"/>'s array, using its own count as the
    /// used length. The dominant shape in the backends' pending-send queues.</summary>
    public static void ReturnCleared(in ArraySegment<byte> segment)
        => ReturnCleared(segment.Array, segment.Offset + segment.Count);
}
