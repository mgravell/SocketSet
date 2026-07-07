using System.Net;
using System.Runtime.InteropServices;

namespace FastNet.WinRio
{
    internal unsafe partial class RioEngine
    {
        private delegate* unmanaged[Stdcall]<uint, ref RIO_NOTIFICATION_COMPLETION, IntPtr> RIOCreateCompletionQueue;
        private delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, uint, IntPtr, IntPtr, ulong, IntPtr> RIOCreateRequestQueue;
        private delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr> RIORegisterBuffer;
        private delegate* unmanaged[Stdcall]<IntPtr, ref RIO_BUF, uint, uint, ulong, bool> RIOReceive;
        private delegate* unmanaged[Stdcall]<IntPtr, ref RIO_BUF, uint, uint, ulong, bool> RIOSend;
        private delegate* unmanaged[Stdcall]<IntPtr, RIORESULT*, uint, uint> RIODequeueCompletion;
        private delegate* unmanaged[Stdcall]<IntPtr, int> RIONotify;
        private delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void*, uint, uint, uint, out uint, NativeOverlapped*, bool> AcceptEx;

        private IntPtr _listenSocketHandle;
        private IntPtr _iocpHandle;
        private IntPtr _rioCompletionQueue;
        private byte[] _giantPinnedBuffer;
        private IntPtr _rioBufferId;

        private const uint SLAB_SIZE = 8192;
        private const uint MAX_CONNECTIONS = 8192;
        private const uint BUFFER_SIZE = 4096;

        private UnmanagedSlabAllocator _slabAllocator;
        private static readonly UIntPtr CK_RIO_NOTIFY = new UIntPtr(1);
        private static readonly UIntPtr CK_ACCEPT_EVENT = new UIntPtr(2);

        public RioEngine(int port, int workerThreadCount = 4)
        {
            if (WSAStartup(0x0202, out WSADATA wsaData) != 0)
                throw new Exception($"WSAStartup failed with error: {Marshal.GetLastWin32Error()}");

            _listenSocketHandle = WSASocket(2, 1, 6, IntPtr.Zero, 0, WSA_FLAG_REGISTERED_IO | WSA_FLAG_OVERLAPPED);
            if (_listenSocketHandle == IntPtr.Zero || _listenSocketHandle == new IntPtr(-1))
                throw new Exception($"Failed to create native socket. Error: {Marshal.GetLastWin32Error()}");

            RIO_EXTENSION_FUNCTION_TABLE* pRioTable = (RIO_EXTENSION_FUNCTION_TABLE*)NativeMemory.AllocZeroed((nuint)sizeof(RIO_EXTENSION_FUNCTION_TABLE));
            pRioTable->cbSize = (uint)sizeof(RIO_EXTENSION_FUNCTION_TABLE);
            int bytesReturned;

            fixed (Guid* pRioGuid = &WSAID_MULTIPLE_RIO_FUNCTIONS)
            {
                if (WSAIoctl(_listenSocketHandle, SIO_GET_MULTIPLE_EXTENSION_FUNCTION_POINTER,
                    pRioGuid, Marshal.SizeOf(typeof(Guid)),
                    pRioTable, sizeof(RIO_EXTENSION_FUNCTION_TABLE),
                    out bytesReturned, IntPtr.Zero, IntPtr.Zero) != 0)
                {
                    NativeMemory.Free(pRioTable);
                    throw new Exception($"Failed to fetch RIO Table. True Win32 Error: {Marshal.GetLastWin32Error()}");
                }
            }

            RIOCreateCompletionQueue = (delegate* unmanaged[Stdcall]<uint, ref RIO_NOTIFICATION_COMPLETION, IntPtr>)pRioTable->RIOCreateCompletionQueue;
            RIOCreateRequestQueue = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, uint, IntPtr, IntPtr, ulong, IntPtr>)pRioTable->RIOCreateRequestQueue;
            RIORegisterBuffer = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr>)pRioTable->RIORegisterBuffer;
            RIOReceive = (delegate* unmanaged[Stdcall]<IntPtr, ref RIO_BUF, uint, uint, ulong, bool>)pRioTable->RIOReceive;
            RIOSend = (delegate* unmanaged[Stdcall]<IntPtr, ref RIO_BUF, uint, uint, ulong, bool>)pRioTable->RIOSend;
            RIODequeueCompletion = (delegate* unmanaged[Stdcall]<IntPtr, RIORESULT*, uint, uint>)pRioTable->RIODequeueCompletion;
            RIONotify = (delegate* unmanaged[Stdcall]<IntPtr, int>)pRioTable->RIONotify;

            NativeMemory.Free(pRioTable);

            IntPtr rawAcceptExPointer = IntPtr.Zero;
            fixed (Guid* pAcceptGuid = &WSAID_ACCEPTEX)
            {
                if (WSAIoctlAddr(_listenSocketHandle, SIO_GET_EXTENSION_FUNCTION_POINTER,
                    pAcceptGuid, Marshal.SizeOf(typeof(Guid)),
                    &rawAcceptExPointer, sizeof(IntPtr),
                    out bytesReturned, IntPtr.Zero, IntPtr.Zero) != 0)
                {
                    throw new Exception($"Failed to fetch AcceptEx function pointer. True Win32 Error: {Marshal.GetLastWin32Error()}");
                }
            }
            AcceptEx = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void*, uint, uint, uint, out uint, NativeOverlapped*, bool>)rawAcceptExPointer;

            _iocpHandle = CreateIoCompletionPort(new IntPtr(-1), IntPtr.Zero, UIntPtr.Zero, (uint)workerThreadCount);
            if (_iocpHandle == IntPtr.Zero)
                throw new Exception($"Failed to create IOCP port. Error: {Marshal.GetLastWin32Error()}");

            CreateIoCompletionPort(_listenSocketHandle, _iocpHandle, CK_ACCEPT_EVENT, 0);

            // FIX: Remapped initialization fields targeting our nested parameter parameters smoothly
            var notificationConfig = new RIO_NOTIFICATION_COMPLETION
            {
                Type = 2, // FIX: 2 = RIO_IOCP_COMPLETION (1 is for standard Event notification objects)
                Iocp = new RIO_NOTIFICATION_COMPLETION_IOCP
                {
                    IocpHandle = _iocpHandle,
                    CompletionKey = (IntPtr)CK_RIO_NOTIFY.ToUInt64(),
                    Overlapped = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(NativeOverlapped)))
                }
            };

            _rioCompletionQueue = RIOCreateCompletionQueue(MAX_CONNECTIONS * 2, ref notificationConfig);
            if (_rioCompletionQueue == IntPtr.Zero)
                throw new Exception($"Failed to create RIO completion queue. Error: {Marshal.GetLastWin32Error()}");

            _giantPinnedBuffer = GC.AllocateArray<byte>((int)(MAX_CONNECTIONS * SLAB_SIZE), pinned: true);
            fixed (byte* pBuffer = _giantPinnedBuffer)
            {
                _rioBufferId = RIORegisterBuffer((IntPtr)pBuffer, (uint)_giantPinnedBuffer.Length);
            }
            if (_rioBufferId == new IntPtr(-1))
                throw new Exception($"Failed to register giant memory buffer block. Error: {Marshal.GetLastWin32Error()}");

            _slabAllocator = new UnmanagedSlabAllocator(MAX_CONNECTIONS, SLAB_SIZE);

            var bindAddr = new SockAddrIn { sin_family = 2, sin_port = (ushort)IPAddress.HostToNetworkOrder((short)port), sin_addr = 0 };
            if (bind(_listenSocketHandle, ref bindAddr, Marshal.SizeOf(typeof(SockAddrIn))) != 0)
                throw new Exception($"Native bind failed. Error: {Marshal.GetLastWin32Error()}");

            if (listen(_listenSocketHandle, 500) != 0)
                throw new Exception($"Native listen failed. Error: {Marshal.GetLastWin32Error()}");

            for (int i = 0; i < workerThreadCount * 2; i++) PostNewAcceptEx();
            for (int i = 0; i < workerThreadCount; i++) new Thread(WorkerThreadLoop) { IsBackground = true, Name = $"RIO-Worker-{i}" }.Start();
        }
    }
}
