using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Socketizer;

internal static partial class Winsock
{

    const string WS2_32 = "ws2_32.dll";

    [LibraryImport(WS2_32, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr WSASocketW(
           AddressFamily addressFamily,
           SocketType socketType,
           ProtocolType protocolType,
           IntPtr protocolInfo,
           int group,
           int flags);

    [LibraryImport(WS2_32)]
    internal static unsafe partial SocketError WSAConnect(
        IntPtr socketHandle,
        byte* socketAddress,
        int socketAddressSize,
        IntPtr inBuffer,
        IntPtr outBuffer,
        IntPtr sQOS,
        IntPtr gQOS);


    [LibraryImport(WS2_32)]
    internal static partial SocketError ioctlsocket(
            IntPtr socketHandle,
            int cmd,
            ref int argp);

    [LibraryImport(WS2_32)]
    internal static unsafe partial SocketError WSAStartup(short wVersionRequested, WSAData* lpWSAData);

    [StructLayout(LayoutKind.Sequential, Size = 408)]
    internal struct WSAData
    {
        // unused, just needs to be large enough
    }


    [LibraryImport(WS2_32)]
    internal static unsafe partial SocketError WSARecv(
        IntPtr socketHandle,
        WSABuffer* buffer,
        int bufferCount,
        out int bytesTransferred,
        ref SocketFlags socketFlags,
        NativeOverlapped* overlapped,
        delegate* unmanaged[Stdcall]<SocketError, int, NativeOverlapped*, SocketFlags, void> completionRoutine);

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct WSABuffer(int length, IntPtr pointer)
    {
        public readonly int Length = length; // Length of Buffer
        public readonly IntPtr Pointer = pointer; // Pointer to Buffer
    }

    [LibraryImport(WS2_32)]
    internal static unsafe partial int select(
        int nfds,
        IntPtr* readfds,
        IntPtr* writefds,
        IntPtr* exceptfds,
        TimeValue* timeout);

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct TimeValue(int seconds, int microseconds)
    {
        public readonly int Seconds = seconds;
        public readonly int Microseconds = microseconds;
    }

    [LibraryImport(WS2_32)]
    internal static unsafe partial SocketError WSASend(
       IntPtr socketHandle,
       WSABuffer* buffers,
       int bufferCount,
       out int bytesTransferred,
       SocketFlags socketFlags,
       NativeOverlapped* overlapped,
       delegate* unmanaged[Stdcall]<SocketError, int, NativeOverlapped*, SocketFlags, void> completionRoutine);

    [LibraryImport(WS2_32)]
    internal static partial IntPtr WSACreateEvent();

    [LibraryImport(WS2_32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WSACloseEvent(IntPtr eventHandle);

    [LibraryImport(WS2_32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WSAResetEvent(IntPtr eventHandle);

    internal static SocketError GetLastSocketError()
    {
        int win32Error = WSAGetLastError(); // Marshal.GetLastPInvokeError();
        Debug.Assert(win32Error != 0, "Expected non-0 error");
        return (SocketError)win32Error;
    }

    [LibraryImport(WS2_32)]
    internal static partial int WSAGetLastError();

    [MethodImpl(MethodImplOptions.NoInlining), DoesNotReturn]
    internal static void ThrowLastSocketError()
       => throw new SocketException((int)GetLastSocketError());

    [MethodImpl(MethodImplOptions.NoInlining), DoesNotReturn]
    internal static void ThrowSocketError(SocketError error)
    {
        Debug.Assert(error != SocketError.Success);
        if (error == SocketError.SocketError)
        {
            ThrowLastSocketError();
        }
        throw new SocketException((int)error);
    }

    internal static unsafe SocketError Read(IntPtr socketHandle, WSABuffer* readBuffer, out int bytes)
    {
        SocketFlags flags = SocketFlags.None;
        var error = WSARecv(socketHandle, readBuffer, 1, out bytes, ref flags, null, null);
        switch (error)
        {
            case SocketError.Success:
                return SocketError.Success;
            case SocketError.SocketError:
                bytes = 0;
                return GetLastSocketError();
            default:
                bytes = 0;
                return error;
        }
    }

    /*
    public unsafe static readonly delegate* unmanaged[Stdcall]<int, int, NativeOverlapped*, SocketFlags, void> OnRead = &ReadCallback;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe void ReadCallback(int status, int bytesTransferred, NativeOverlapped* overlapped, SocketFlags flags)
    {
        throw new NotImplementedException();
    }
    */
}
