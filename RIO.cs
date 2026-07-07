using System;
using System.Net;
using System.Runtime.InteropServices;
using FastNet.Abstraction;

namespace FastNet.WinRio;

public unsafe class RioEngine : IOEngine
{
    private const uint SIO_GET_EXTENSION_FUNCTION_POINTER = 0xC8000014;
    private static readonly Guid WSAID_MULTIPLE_RIO_FUNCTIONS = new Guid("8509a001-96d7-40a6-b14f-32945da7ec2e");

    [StructLayout(LayoutKind.Sequential)]
    private struct RIO_BUF
    {
        public IntPtr BufferId;
        public uint Offset;
        public uint Length;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RIORESULT
    {
        public int Status;
        public uint BytesTransferred;
        public ulong SocketContext;
        public ulong RequestContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SOCKADDR_IN
    {
        public short sin_family;
        public ushort sin_port;
        public uint sin_addr;
        public ulong sin_zero;
    }

    // Layout mapping exactly to the structural layout exported by mswsock.dll
    [StructLayout(LayoutKind.Sequential)]
    private struct RIO_EXTENSION_FUNCTION_TABLE
    {
        public delegate* unmanaged[Stdcall, SuppressGCTransition]<IntPtr, RIO_BUF*, uint, uint, ulong, bool> RIOReceive;
        public IntPtr RIOReceiveEx;
        public delegate* unmanaged[Stdcall, SuppressGCTransition]<IntPtr, RIO_BUF*, uint, uint, ulong, bool> RIOSend;
        public IntPtr RIOSendEx;
        public delegate* unmanaged[Stdcall, SuppressGCTransition]<IntPtr, void> RIOCloseCompletionQueue;
        public delegate* unmanaged[Stdcall, SuppressGCTransition]<uint, IntPtr, IntPtr> RIOCreateCompletionQueue;

        public delegate* unmanaged[Stdcall, SuppressGCTransition]<IntPtr, uint, uint, uint, uint, IntPtr, IntPtr, ulong,
            IntPtr> RIOCreateRequestQueue;

        public delegate* unmanaged[Stdcall, SuppressGCTransition]<IntPtr, void> RIODeregisterBuffer;
        public delegate* unmanaged[Stdcall, SuppressGCTransition]<IntPtr, int> RIONotify;
        public delegate* unmanaged[Stdcall, SuppressGCTransition]<byte[], uint, IntPtr> RIORegisterBuffer;
        public IntPtr RIOResizeCompletionQueue;
        public IntPtr RIOResizeRequestQueue;
    }

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern IntPtr socket(int af, int type, int protocol);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int bind(IntPtr s, ref SOCKADDR_IN name, int namelen);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int listen(IntPtr s, int backlog);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int WSAIoctl(
        IntPtr s, uint dwIoControlCode, ref Guid lpvInBuffer, int cbInBuffer,
        ref RIO_EXTENSION_FUNCTION_TABLE lpvOutBuffer, int cbOutBuffer,
        out uint lpcbBytesReturned, IntPtr lpOverlapped, IntPtr lpCompletionRoutine);

    private delegate* unmanaged[Stdcall, SuppressGCTransition]<IntPtr, RIO_BUF*, uint, uint, ulong, bool> _rioReceive;
    private delegate* unmanaged[Stdcall, SuppressGCTransition]<IntPtr, RIO_BUF*, uint, uint, ulong, bool> _rioSend;
    private delegate* unmanaged[Stdcall, SuppressGCTransition]<IntPtr, RIORESULT*, uint, uint> _rioDequeueResults;

    private IntPtr _listenerSocket;
    private IntPtr _completionQueue;
    private IntPtr _registeredBufferId;
    private GCHandle _megaBufferHandle;
    private int _bufferSize;

    public void Initialize(IPEndPoint endpoint, int maxConnections, int bufferSize)
    {
        _bufferSize = bufferSize;

        _listenerSocket = socket(2, 1, 6); // AF_INET, SOCK_STREAM, IPPROTO_TCP
        if (_listenerSocket == (IntPtr)(-1))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

        var addr = new SOCKADDR_IN
        {
            sin_family = 2,
            sin_port = (ushort)IPAddress.HostToNetworkOrder((short)endpoint.Port),
            sin_addr = BitConverter.ToUInt32(endpoint.Address.GetAddressBytes(), 0)
        };

        if (bind(_listenerSocket, ref addr, Marshal.SizeOf(addr)) != 0)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        if (listen(_listenerSocket, maxConnections) != 0)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

        var rioTable = new RIO_EXTENSION_FUNCTION_TABLE();
        uint bytesReturned;
        if (WSAIoctl(_listenerSocket, SIO_GET_EXTENSION_FUNCTION_POINTER, ref WSAID_MULTIPLE_RIO_FUNCTIONS,
                Marshal.SizeOf(WSAID_MULTIPLE_RIO_FUNCTIONS), ref rioTable, Marshal.SizeOf(rioTable),
                out bytesReturned, IntPtr.Zero, IntPtr.Zero) != 0)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }

        // Extract function pointers from table layout slots
        _rioReceive = rioTable.RIOReceive;
        _rioSend = rioTable.RIOSend;

        // RIODequeueResults sits at exactly index 11 within the unmanaged extension memory table block
        IntPtr* ptrTable = (IntPtr*)&rioTable;
        _rioDequeueResults =
            (delegate* unmanaged[Stdcall, SuppressGCTransition]<IntPtr, RIORESULT*, uint, uint>)ptrTable[11];

        _completionQueue = rioTable.RIOCreateCompletionQueue(1024, IntPtr.Zero);
        if (_completionQueue == IntPtr.Zero) throw new Exception("Could not allocate RIO CQ.");

        Console.WriteLine("[RIO Engine] Native unmanaged function pointer engine running.");
    }

    public void RegisterBuffers(byte[] megaBuffer)
    {
        _megaBufferHandle = GCHandle.Alloc(megaBuffer, GCHandleType.Pinned);

        var rioTable = new RIO_EXTENSION_FUNCTION_TABLE();
        uint bytesReturned;
        WSAIoctl(_listenerSocket, SIO_GET_EXTENSION_FUNCTION_POINTER, ref WSAID_MULTIPLE_RIO_FUNCTIONS,
            Marshal.SizeOf(WSAID_MULTIPLE_RIO_FUNCTIONS), ref rioTable, Marshal.SizeOf(rioTable),
            out bytesReturned, IntPtr.Zero, IntPtr.Zero);

        _registeredBufferId = rioTable.RIORegisterBuffer(megaBuffer, (uint)megaBuffer.Length);
        if (_registeredBufferId == IntPtr.Zero) throw new Exception("Kernel buffer registration failed.");
    }

    public void PostAccept()
    {
        // Leveraged independently via AcceptEx/WSAAccept loops
    }

    public void PostReceive(object contextToken, BufferSlice slice)
    {
        IntPtr queueHandle = contextToken is IntPtr handle ? handle : IntPtr.Zero;
        if (queueHandle == IntPtr.Zero) return;

        var buf = new RIO_BUF
        {
            BufferId = _registeredBufferId,
            Offset = (uint)slice.Offset,
            Length = (uint)slice.Length
        };

        ulong requestContext = PackToken(OpType.Receive, slice.Id, slice.Offset);
        _rioReceive(queueHandle, &buf, 1, 0, requestContext);
    }

    public void PostSend(object contextToken, BufferSlice slice)
    {
        IntPtr queueHandle = contextToken is IntPtr handle ? handle : IntPtr.Zero;
        if (queueHandle == IntPtr.Zero) return;

        var buf = new RIO_BUF
        {
            BufferId = _registeredBufferId,
            Offset = (uint)slice.Offset,
            Length = (uint)slice.Length
        };

        ulong requestContext = PackToken(OpType.Send, slice.Id, slice.Offset);
        _rioSend(queueHandle, &buf, 1, 0, requestContext);
    }

    public void PollCompletions(Action<AsyncResult> onComplete)
    {
        const int batchSize = 32;
        RIORESULT* results = stackalloc RIORESULT[batchSize];

        uint count = _rioDequeueResults(_completionQueue, results, batchSize);
        for (uint i = 0; i < count; i++)
        {
            RIORESULT* res = &results[i];
            UnpackToken(res->RequestContext, out OpType op, out int sliceId, out int offset);

            var finalResult = new AsyncResult
            {
                Operation = op,
                NativeHandle = (IntPtr)res->SocketContext,
                ManagedContext = null,
                BytesTransferred = (int)res->BytesTransferred,
                Success = res->Status == 0,
                Slice = new BufferSlice { Id = sliceId, Offset = offset, Length = _bufferSize }
            };

            onComplete(finalResult);
        }
    }

    // --- Clean, Parser-Safe Context Packaging ---
    private static ulong PackToken(OpType op, int id, int offset)
    {
        ulong shiftOp = (ulong)op << 48;
        ulong maskId = (ulong)(id & 0xFFFF);
        ulong shiftId = maskId << 32;
        ulong castOffset = (ulong)(uint)offset;

        return shiftOp | shiftId | castOffset;
    }

    private static void UnpackToken(ulong token, out OpType op, out int id, out int offset)
    {
        ulong opVal = token >> 48;
        op = (OpType)opVal;

        ulong idVal = token >> 32;
        id = (int)(idVal & 0xFFFF);

        uint offsetVal = (uint)token;
        offset = (int)offsetVal;
    }

    public void Dispose()
    {
        if (_megaBufferHandle.IsAllocated) _megaBufferHandle.Free();
    }
}