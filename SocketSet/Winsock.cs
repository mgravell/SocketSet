﻿using System.Diagnostics;
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

    [LibraryImport("libSystem.Native", EntryPoint = "SystemNative_Poll")]
    internal static unsafe partial Error Poll(PollEvent* pollEvents, uint eventCount, int timeout, uint* triggered);

    [LibraryImport("libSystem.Native", EntryPoint = "SystemNative_Select")]
    internal static unsafe partial Error Select(Span<int> readFDs, int readFDsLength, Span<int> writeFDs, int writeFDsLength,  Span<int> checkError, int checkErrorLength, int timeout, int maxFd, out int triggered);

     [Flags]
    internal enum PollEvents : short
    {
        POLLNONE = 0x0000,  // No events occurred.
        POLLIN   = 0x0001,  // non-urgent readable data available
        POLLPRI  = 0x0002,  // urgent readable data available
        POLLOUT  = 0x0004,  // data can be written without blocked
        POLLERR  = 0x0008,  // an error occurred
        POLLHUP  = 0x0010,  // the file descriptor hung up
        POLLNVAL = 0x0020,  // the requested events were invalid
    }

    internal struct PollEvent
    {
        internal int FileDescriptor;         // The file descriptor to poll
        internal PollEvents Events;          // The events to poll for
        internal PollEvents TriggeredEvents; // The events that occurred which triggered the poll
    }

    /// <summary>Common Unix errno error codes.</summary>
    internal enum Error
    {
        // These values were defined in src/Native/System.Native/fxerrno.h
        //
        // They compare against values obtained via Interop.Sys.GetLastError() not Marshal.GetLastPInvokeError()
        // which obtains the raw errno that varies between unixes. The strong typing as an enum is meant to
        // prevent confusing the two. Casting to or from int is suspect. Use GetLastErrorInfo() if you need to
        // correlate these to the underlying platform values or obtain the corresponding error message.
        //

        SUCCESS          = 0,

        E2BIG            = 0x10001,           // Argument list too long.
        EACCES           = 0x10002,           // Permission denied.
        EADDRINUSE       = 0x10003,           // Address in use.
        EADDRNOTAVAIL    = 0x10004,           // Address not available.
        EAFNOSUPPORT     = 0x10005,           // Address family not supported.
        EAGAIN           = 0x10006,           // Resource unavailable, try again (same value as EWOULDBLOCK),
        EALREADY         = 0x10007,           // Connection already in progress.
        EBADF            = 0x10008,           // Bad file descriptor.
        EBADMSG          = 0x10009,           // Bad message.
        EBUSY            = 0x1000A,           // Device or resource busy.
        ECANCELED        = 0x1000B,           // Operation canceled.
        ECHILD           = 0x1000C,           // No child processes.
        ECONNABORTED     = 0x1000D,           // Connection aborted.
        ECONNREFUSED     = 0x1000E,           // Connection refused.
        ECONNRESET       = 0x1000F,           // Connection reset.
        EDEADLK          = 0x10010,           // Resource deadlock would occur.
        EDESTADDRREQ     = 0x10011,           // Destination address required.
        EDOM             = 0x10012,           // Mathematics argument out of domain of function.
        EDQUOT           = 0x10013,           // Reserved.
        EEXIST           = 0x10014,           // File exists.
        EFAULT           = 0x10015,           // Bad address.
        EFBIG            = 0x10016,           // File too large.
        EHOSTUNREACH     = 0x10017,           // Host is unreachable.
        EIDRM            = 0x10018,           // Identifier removed.
        EILSEQ           = 0x10019,           // Illegal byte sequence.
        EINPROGRESS      = 0x1001A,           // Operation in progress.
        EINTR            = 0x1001B,           // Interrupted function.
        EINVAL           = 0x1001C,           // Invalid argument.
        EIO              = 0x1001D,           // I/O error.
        EISCONN          = 0x1001E,           // Socket is connected.
        EISDIR           = 0x1001F,           // Is a directory.
        ELOOP            = 0x10020,           // Too many levels of symbolic links.
        EMFILE           = 0x10021,           // File descriptor value too large.
        EMLINK           = 0x10022,           // Too many links.
        EMSGSIZE         = 0x10023,           // Message too large.
        EMULTIHOP        = 0x10024,           // Reserved.
        ENAMETOOLONG     = 0x10025,           // Filename too long.
        ENETDOWN         = 0x10026,           // Network is down.
        ENETRESET        = 0x10027,           // Connection aborted by network.
        ENETUNREACH      = 0x10028,           // Network unreachable.
        ENFILE           = 0x10029,           // Too many files open in system.
        ENOBUFS          = 0x1002A,           // No buffer space available.
        ENODEV           = 0x1002C,           // No such device.
        ENOENT           = 0x1002D,           // No such file or directory.
        ENOEXEC          = 0x1002E,           // Executable file format error.
        ENOLCK           = 0x1002F,           // No locks available.
        ENOLINK          = 0x10030,           // Reserved.
        ENOMEM           = 0x10031,           // Not enough space.
        ENOMSG           = 0x10032,           // No message of the desired type.
        ENOPROTOOPT      = 0x10033,           // Protocol not available.
        ENOSPC           = 0x10034,           // No space left on device.
        ENOSYS           = 0x10037,           // Function not supported.
        ENOTCONN         = 0x10038,           // The socket is not connected.
        ENOTDIR          = 0x10039,           // Not a directory or a symbolic link to a directory.
        ENOTEMPTY        = 0x1003A,           // Directory not empty.
        ENOTRECOVERABLE  = 0x1003B,           // State not recoverable.
        ENOTSOCK         = 0x1003C,           // Not a socket.
        ENOTSUP          = 0x1003D,           // Not supported (same value as EOPNOTSUP).
        ENOTTY           = 0x1003E,           // Inappropriate I/O control operation.
        ENXIO            = 0x1003F,           // No such device or address.
        EOVERFLOW        = 0x10040,           // Value too large to be stored in data type.
        EOWNERDEAD       = 0x10041,           // Previous owner died.
        EPERM            = 0x10042,           // Operation not permitted.
        EPIPE            = 0x10043,           // Broken pipe.
        EPROTO           = 0x10044,           // Protocol error.
        EPROTONOSUPPORT  = 0x10045,           // Protocol not supported.
        EPROTOTYPE       = 0x10046,           // Protocol wrong type for socket.
        ERANGE           = 0x10047,           // Result too large.
        EROFS            = 0x10048,           // Read-only file system.
        ESPIPE           = 0x10049,           // Invalid seek.
        ESRCH            = 0x1004A,           // No such process.
        ESTALE           = 0x1004B,           // Reserved.
        ETIMEDOUT        = 0x1004D,           // Connection timed out.
        ETXTBSY          = 0x1004E,           // Text file busy.
        EXDEV            = 0x1004F,           // Cross-device link.
        ESOCKTNOSUPPORT  = 0x1005E,           // Socket type not supported.
        EPFNOSUPPORT     = 0x10060,           // Protocol family not supported.
        ESHUTDOWN        = 0x1006C,           // Socket shutdown.
        EHOSTDOWN        = 0x10070,           // Host is down.
        ENODATA          = 0x10071,           // No data available.

        // Custom Error codes to track errors beyond kernel interface.
        EHOSTNOTFOUND    = 0x20001,           // Name lookup failed
        ESOCKETERROR     = 0x20002,           // Unspecified socket error

        // POSIX permits these to have the same value and we make them always equal so
        // that we do not introduce a dependency on distinguishing between them that
        // would not work on all platforms.
        EOPNOTSUPP      = ENOTSUP,            // Operation not supported on socket.
        EWOULDBLOCK     = EAGAIN,             // Operation would block.
    }
}
