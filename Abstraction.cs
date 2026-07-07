using System;
using System.Net;

namespace FastNet.Abstraction;

// Identifies a specific pre-registered memory slice for zero-copy IO
public struct BufferSlice
{
    public int Id;
    public int Offset;
    public int Length;
}

// High-level interface for asynchronous socket operations
public interface IOEngine : IDisposable
{
    void Initialize(IPEndPoint endpoint, int maxConnections, int bufferSize);
    void RegisterBuffers(byte[] megaBuffer);
    void PostAccept();
    void PostReceive(IntPtr socketContext, BufferSlice slice);
    void PostSend(IntPtr socketContext, BufferSlice slice);
    void PollCompletions(Action<AsyncResult> onComplete);
}

public enum OpType
{
    Accept,
    Receive,
    Send
}

public struct AsyncResult
{
    public OpType Operation;
    public int BytesTransferred;
    public BufferSlice Slice;
    public bool Success;
    public nint NativeHandle { get; set; }
    public object? ManagedContext { get; set; }
}