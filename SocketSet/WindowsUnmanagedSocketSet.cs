using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static Socketizer.Winsock;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace Socketizer;

internal partial class WindowsUnmanagedSocketSet : SocketSet
{
    public WindowsUnmanagedSocketSet(ReadCallback onRead) : base(onRead)
    {
        EnsureInit();
        var thread = new Thread(DedicatedLoop)
        {
            Priority = ThreadPriority.AboveNormal,
            Name = "UnmanagedSocketPoll"
        };
        thread.Start();
    }

    static void EnsureInit()
    {
        if (Volatile.Read(ref s_InitCount) == 0) // note fine to do multiple times
        {
            WSAData d;
            const int VERSION = 0x0202; // 2.2
            SocketError err;
            unsafe
            {
                err = WSAStartup(VERSION, &d);
            }
            if (err != SocketError.Success) ThrowSocketError(err);

            // do this last to avoid race with a second instance attempting to do things
            // before we're done
            Interlocked.Increment(ref s_InitCount);
        }
    }

    private unsafe void DedicatedLoop()
    {
        List<IntPtr> read = [];
        var pulseTimeoutMilliseconds = 1000;
        TimeValue selectTimeout = new(seconds: 0, microseconds: 50 * 1000);
        byte[] pinnedBuffer = GC.AllocateArray<byte>(8 * 1024, pinned: true);
        WSABuffer readBuffer = new(pinnedBuffer.Length, new(Unsafe.AsPointer(ref pinnedBuffer[0])));

        while (!IsDisposed)
        {
            Span<IntPtr> reads;
            lock (_pendingRead)
            {
                if (_pendingRead.Count == 0)
                {
                    Monitor.Wait(_pendingRead, pulseTimeoutMilliseconds);
                    continue;
                }
                CollectionsMarshal.SetCount(read, _pendingRead.Count + 1);
                reads = CollectionsMarshal.AsSpan(read);
                reads[0] = _pendingRead.Count;
                CollectionsMarshal.AsSpan(_pendingRead).CopyTo(reads.Slice(1));
            }

            fixed (IntPtr* ptr = reads)
            {
                int count = select(0, ptr, null, null, &selectTimeout);
                if (count == 0) continue;
                if (count < 0) ThrowLastSocketError();
                reads = reads.Slice(0, count); // active handles
            }

            foreach (var socket in CollectionsMarshal.AsSpan(read))
            {
                bool readAgain = false;
                if (children.TryGetValue(socket, out var child))
                {
                    SocketError error = Winsock.Read(socket, &readBuffer, out int bytes);
                    try
                    {
                        readAgain = OnRead(child, error, bytes > 0 ? pinnedBuffer.AsSpan(0, bytes) : default)
                            & error == SocketError.Success;
                    }
                    catch { }
                }
                if (!readAgain)
                {
                    lock (_pendingRead)
                    {
                        _pendingRead.Remove(socket);
                    }
                }
            }
        }
    }

    private static int s_InitCount;

    public sealed class UnmanagedWindowsSocket(WindowsUnmanagedSocketSet owner, IntPtr handle, object? userToken) : SocketBase(owner, userToken)
    {
        internal readonly IntPtr Handle = handle;
    }

    private readonly ConcurrentDictionary<IntPtr, UnmanagedWindowsSocket> children = [];

    public override SocketBase Open(EndPoint endpoint, object? userToken = null, bool read = true)
    {
        const int WSA_FLAG_OVERLAPPED = 0x01;
        const int WSA_FLAG_NO_HANDLE_INHERIT = 0x80;
        const nint INVALID_SOCKET = ~0;
        ThrowIfDisposed();
        var handle = WSASocketW(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp, IntPtr.Zero, 0, WSA_FLAG_OVERLAPPED | WSA_FLAG_NO_HANDLE_INHERIT);

        if (handle == INVALID_SOCKET) ThrowLastSocketError();
        var addr = endpoint.Serialize();
        var span = addr.Buffer.Span;
        unsafe
        {
            fixed (byte* ptr = span)
            {
                var err = WSAConnect(handle, ptr, span.Length, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (err != SocketError.Success) ThrowSocketError(err);
            }
        }

        SetBlocking(handle, false);
        var child = new UnmanagedWindowsSocket(this, handle, userToken);
        children.TryAdd(handle, child);
        if (read)
        {
            Read(child);
        }
        return child;
    }

    private readonly List<IntPtr> _pendingRead = [];

    protected override void Read(SocketBase socket)
    {
        ThrowIfDisposed();
        var h = ((UnmanagedWindowsSocket)socket).Handle;
        lock (_pendingRead)
        {
            if (!_pendingRead.Contains(h))
            {
                _pendingRead.Add(h);
                if (_pendingRead.Count == 1)
                {
                    Monitor.Pulse(_pendingRead);
                }
            }
        }
    }

    protected override unsafe void Write(SocketBase socket, ReadOnlySpan<byte> value)
    {
        ThrowIfDisposed();
        Write(((UnmanagedWindowsSocket)socket).Handle, value);
    }

    internal static unsafe void Write(IntPtr socketHandle, ReadOnlySpan<byte> value)
    {
        while (true)
        {
            SocketError err;
            int bytesTransferred;
            fixed (byte* ptr = value)
            {
                WSABuffer writeBuffer = new WSABuffer(value.Length, new(ptr));
                err = WSASend(socketHandle, &writeBuffer, 1, out bytesTransferred, SocketFlags.None, null, IntPtr.Zero);
            }
            if (err != SocketError.Success) ThrowSocketError(err);
            if (bytesTransferred == value.Length) break;
            value = value.Slice(bytesTransferred);
        }
    }

    public static void SetBlocking(IntPtr socketHandle, bool shouldBlock)
    {
        int intBlocking = shouldBlock ? 0 : -1;

        const int FIONBIO = unchecked((int)0x8004667E);

        SocketError errorCode = ioctlsocket(socketHandle, FIONBIO, ref intBlocking);
        if (errorCode != SocketError.Success) ThrowSocketError(errorCode);

        var willBlock = intBlocking == 0;
        if (willBlock != shouldBlock) Throw();
        static void Throw() => throw new InvalidOperationException("Unable to set/unset blocking mode");
    }
}
