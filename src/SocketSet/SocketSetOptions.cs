namespace SocketSets;
public class SocketSetOptions
{
    public int Shards { get; set; } = 4;
    public int SocketsPerShard { get; set; } = 4096;
    public bool PinWorkerThreads { get; set; } = true;
    public SocketSetFactory Factory { get; set; } = SocketSetFactory.Default;
    public int EntriesPerShard { get; set; } = 4096;
    public int BufferPageSize { get; set; } = 4096;
    public int BufferPagesPerShard { get; set; } = 256;
}