using SocketSets.IoUring;

namespace SocketSets;

public abstract class SocketSetFactory
{
    protected SocketSetFactory()
    {
    }

    public static SocketSetFactory IoUring { get; } = IoUringFactory.Instance;
    public static SocketSetFactory Default => IoUring; // for now; OS/feature detection later

    public abstract SocketSetShard CreateShard(SocketSetOptions options);
}
