using System;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;

namespace FastNet.WinRio
{
    internal unsafe partial class RioEngine
    {
        private const uint WSA_FLAG_OVERLAPPED = 0x01;
        private const uint WSA_FLAG_REGISTERED_IO = 0x0100;

        private const int SIO_GET_MULTIPLE_EXTENSION_FUNCTION_POINTER = unchecked((int)0xC8000024);
        private const int SIO_GET_EXTENSION_FUNCTION_POINTER = unchecked((int)0xC8000006);

        private static Guid WSAID_MULTIPLE_RIO_FUNCTIONS = new Guid(0x8509e081, 0x96dd, 0x4005, 0xb1, 0x65, 0x9e, 0x2e, 0xe8, 0xc7, 0x9e, 0x3f);
        private static Guid WSAID_ACCEPTEX = new Guid(0xb5367df1, 0xcbac, 0x11cf, 0x95, 0xca, 0x00, 0x80, 0x5f, 0x48, 0xa1, 0x92);

        private const int SOL_SOCKET = 0xffff;
        private const int SO_UPDATE_ACCEPT_CONTEXT = 0x700B;

        [StructLayout(LayoutKind.Sequential)]
        private struct SockAddrIn
        {
            public short sin_family;
            public ushort sin_port;
            public uint sin_addr;
            public ulong sin_zero;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WSADATA
        {
            public ushort wVersion;
            public ushort wHighVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string szDescription;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 129)] public string szSystemStatus;
            public ushort iMaxSockets;
            public ushort iMaxUdpDg;
            public IntPtr lpVendorInfo;
        }

        [DllImport("ws2_32.dll", SetLastError = true)]
        private static extern int WSAStartup(ushort wVersionRequested, out WSADATA lpWSAData);

        // FIX: Restored raw Winsock error extraction method to query the native unmanaged thread state
        [DllImport("ws2_32.dll", EntryPoint = "WSAGetLastError")]
        private static extern int WSAGetLastError();

        [DllImport("ws2_32.dll", EntryPoint = "WSASocketW", SetLastError = true)]
        private static extern IntPtr WSASocket(int addressFamily, int socketType, int protocol, IntPtr lpProtocolInfo, uint g, uint dwFlags);

        [DllImport("ws2_32.dll", SetLastError = true)]
        private static extern int bind(IntPtr s, ref SockAddrIn name, int namelen);

        [DllImport("ws2_32.dll", SetLastError = true)]
        private static extern int listen(IntPtr s, int backlog);

        [DllImport("ws2_32.dll", SetLastError = true)]
        private static extern int WSAIoctl(IntPtr s, int dwIoControlCode, void* lpvInBuffer, int cbInBuffer, void* lpvOutBuffer, int cbOutBuffer, out int lpcbBytesReturned, IntPtr lpOverlapped, IntPtr lpCompletionRoutine);

        [DllImport("ws2_32.dll", EntryPoint = "WSAIoctl", SetLastError = true)]
        private static extern int WSAIoctlAddr(IntPtr s, int dwIoControlCode, void* lpvInBuffer, int cbInBuffer, IntPtr* lpvOutBuffer, int cbOutBuffer, out int lpcbBytesReturned, IntPtr lpOverlapped, IntPtr lpCompletionRoutine);

        // FIX: Restored standard void* layout support to avoid reference constraints on raw handle mutations
        [DllImport("ws2_32.dll", SetLastError = true)]
        private static extern int setsockopt(IntPtr s, int level, int optname, void* optval, int optlen);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateIoCompletionPort(IntPtr FileHandle, IntPtr ExistingCompletionPort, UIntPtr CompletionKey, uint NumberOfConcurrentThreads);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetQueuedCompletionStatus(IntPtr CompletionPort, out uint lpNumberOfBytesTransferred, out UIntPtr lpCompletionKey, out NativeOverlapped* lpOverlapped, uint dwMilliseconds);

        [StructLayout(LayoutKind.Sequential)]
        private struct RIO_EXTENSION_FUNCTION_TABLE
        {
            public uint cbSize;
            public IntPtr RIOReceive;
            public IntPtr RIOReceiveEx;
            public IntPtr RIOSend;
            public IntPtr RIOSendEx;
            public IntPtr RIOCloseCompletionQueue;
            public IntPtr RIOCreateCompletionQueue;
            public IntPtr RIOCreateRequestQueue;
            public IntPtr RIODequeueCompletion;
            public IntPtr RIODeregisterBuffer;
            public IntPtr RIONotify;
            public IntPtr RIORegisterBuffer;
            public IntPtr RIOResizeCompletionQueue;
            public IntPtr RIOResizeRequestQueue;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RIO_NOTIFICATION_COMPLETION_IOCP
        {
            public IntPtr IocpHandle;
            public IntPtr CompletionKey;
            public IntPtr Overlapped;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RIO_NOTIFICATION_COMPLETION
        {
            public uint Type;
            public RIO_NOTIFICATION_COMPLETION_IOCP Iocp;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RIO_BUF { public IntPtr BufferId; public uint Offset; public uint Length; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RIORESULT { public int Status; public uint BytesTransferred; public ulong ConnectionCorrelation; public ulong RequestCorrelation; }

        [StructLayout(LayoutKind.Sequential)]
        private struct AcceptOverlappedContext
        {
            public NativeOverlapped Overlapped;
            public IntPtr ClientSocketHandle;
            public fixed byte AddressBuffer[64]; // Explicitly declared size bounds to avoid array parsing drops
        }

        private struct ConnectionContext
        {
            public IntPtr RequestQueue;
            public uint RecvOffset;
            public uint SendOffset;
        }

        private class UnmanagedSlabAllocator
        {
            private readonly uint _slabSize;
            private int* _freeIndicesStack;
            private int _topIndex = -1;

            public UnmanagedSlabAllocator(uint totalSlabs, uint slabSize)
            {
                _slabSize = slabSize;
                _freeIndicesStack = (int*)NativeMemory.Alloc(totalSlabs, sizeof(int));
                for (int i = 0; i < totalSlabs; i++) _freeIndicesStack[i] = i;
                _topIndex = (int)totalSlabs - 1;
            }

            public bool TryAllocate(out uint offset)
            {
                while (true)
                {
                    int currentTop = Volatile.Read(ref _topIndex);
                    if (currentTop < 0) { offset = 0; return false; }
                    int index = _freeIndicesStack[currentTop];
                    if (Interlocked.CompareExchange(ref _topIndex, currentTop - 1, currentTop) == currentTop)
                    {
                        offset = (uint)index * _slabSize;
                        return true;
                    }
                }
            }

            public void Free(uint offset)
            {
                int index = (int)(offset / _slabSize);
                while (true)
                {
                    int currentTop = Volatile.Read(ref _topIndex);
                    int nextTop = currentTop + 1;
                    _freeIndicesStack[nextTop] = index;
                    if (Interlocked.CompareExchange(ref _topIndex, nextTop, currentTop) == currentTop) return;
                }
            }
        }
    }
}
