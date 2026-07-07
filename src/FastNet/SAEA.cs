using System;

namespace FastNet.Fallback;

/// <summary>
/// SocketAsyncEventArgs fallback backend — future work. Portable path for
/// platforms without RIO or io_uring (macOS/BSD, older kernels), and the host
/// for the eventual TLS-via-SslStream flow: when TLS is enabled on the upstream
/// we wrap the socket stream in SslStream and drive it through this reactor,
/// accepting the extra copies as the cost of portability.
///
/// Will consume the same BufferPool via SetBuffer(Memory&lt;byte&gt;) over the
/// pinned native block, and present the same connection state machine the
/// io_uring loop uses, so behaviour is identical across backends.
/// </summary>
internal sealed class SaeaEngine
{
    public SaeaEngine() => throw new NotImplementedException(
        "SAEA fallback not implemented yet. io_uring (Transport/EchoServer.cs) is the reference.");
}
