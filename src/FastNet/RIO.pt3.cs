using System.Runtime.InteropServices;

namespace FastNet.WinRio
{
    internal unsafe partial class RioEngine
    {
        private int _dequeueLock = 0;

        private void PostNewAcceptEx()
        {
            IntPtr clientSocketHandle = WSASocket(2, 1, 6, IntPtr.Zero, 0, WSA_FLAG_REGISTERED_IO | WSA_FLAG_OVERLAPPED);
            if (clientSocketHandle == IntPtr.Zero || clientSocketHandle == new IntPtr(-1))
            {
                Console.WriteLine($"[Error] Failed to pre-allocate client socket handle: {WSAGetLastError()}");
                return;
            }

            AcceptOverlappedContext* context = (AcceptOverlappedContext*)Marshal.AllocHGlobal(sizeof(AcceptOverlappedContext));
            NativeMemory.Clear(context, (nuint)sizeof(AcceptOverlappedContext));
            context->ClientSocketHandle = clientSocketHandle;

            uint receivedBytes;

            bool completedInstantly = AcceptEx(
                _listenSocketHandle,
                clientSocketHandle,
                context->AddressBuffer,
                0,
                32,
                32,
                out receivedBytes,
                &context->Overlapped);

            // FIX: Pulling error from WSAGetLastError() to circumvent pointer metadata clearing traps
            int nativeError = WSAGetLastError();
            if (!completedInstantly && nativeError != 997) // 997 = WSA_IO_PENDING
            {
                Console.WriteLine($"[Error] AcceptEx registration failed natively: {nativeError}");
                Marshal.FreeHGlobal((IntPtr)context);
            }
        }

        private void WorkerThreadLoop()
        {
            RIORESULT[] localResults = new RIORESULT[128];
            uint maxResults = (uint)localResults.Length;

            while (true)
            {
                uint bytesTransferred;
                UIntPtr completionKey;
                NativeOverlapped* pOverlapped;

                if (GetQueuedCompletionStatus(_iocpHandle, out bytesTransferred, out completionKey, out pOverlapped, uint.MaxValue))
                {
                    if (completionKey == CK_ACCEPT_EVENT)
                    {
                        AcceptOverlappedContext* context = (AcceptOverlappedContext*)pOverlapped;
                        IntPtr clientHandle = context->ClientSocketHandle;
                        Marshal.FreeHGlobal((IntPtr)context);

                        // FIX: Updated context parsing configuration variables via raw unmanaged void* address
                        IntPtr listenHandleCopy = _listenSocketHandle;
                        setsockopt(clientHandle, SOL_SOCKET, SO_UPDATE_ACCEPT_CONTEXT, &listenHandleCopy, sizeof(IntPtr));

                        if (!_slabAllocator.TryAllocate(out uint sliceOffset))
                        {
                            Console.WriteLine("[Warning] Max connection pool size exhausted. Dropping client connection context.");
                            PostNewAcceptEx();
                            continue;
                        }

                        ConnectionContext* conn = (ConnectionContext*)Marshal.AllocHGlobal(sizeof(ConnectionContext));
                        conn->RecvOffset = sliceOffset;
                        conn->SendOffset = sliceOffset + BUFFER_SIZE;
                        conn->RequestQueue = RIOCreateRequestQueue(clientHandle, 32, 1, 32, 1, _rioCompletionQueue, _rioCompletionQueue, (ulong)conn);

                        if (conn->RequestQueue == IntPtr.Zero)
                        {
                            Console.WriteLine($"[Error] Failed to create RIO Request Queue: {WSAGetLastError()}");
                            _slabAllocator.Free(sliceOffset);
                            Marshal.FreeHGlobal((IntPtr)conn);
                            PostNewAcceptEx();
                            continue;
                        }

                        RIO_BUF receiveBuffer = new RIO_BUF { BufferId = _rioBufferId, Offset = conn->RecvOffset, Length = BUFFER_SIZE };
                        RIONotify(_rioCompletionQueue);
                        RIOReceive(conn->RequestQueue, ref receiveBuffer, 1, 0, 0);

                        PostNewAcceptEx();
                        continue;
                    }

                    if (completionKey == CK_RIO_NOTIFY)
                    {
                        uint completedCount = 0;
                        RIONotify(_rioCompletionQueue);

                        while (Interlocked.CompareExchange(ref _dequeueLock, 1, 0) != 0) { Thread.SpinWait(1); }

                        fixed (RIORESULT* pLocalResults = localResults)
                        {
                            completedCount = RIODequeueCompletion(_rioCompletionQueue, pLocalResults, maxResults);
                            Volatile.Write(ref _dequeueLock, 0);

                            while (completedCount > 0 && completedCount != uint.MaxValue)
                            {
                                for (int i = 0; i < completedCount; i++)
                                {
                                    var result = pLocalResults[i];
                                    ConnectionContext* conn = (ConnectionContext*)result.ConnectionCorrelation;

                                    if (result.Status != 0 || result.BytesTransferred == 0)
                                    {
                                        _slabAllocator.Free(conn->RecvOffset);
                                        Marshal.FreeHGlobal((IntPtr)conn);
                                        continue;
                                    }

                                    if (result.RequestCorrelation == 0) // RECV Completed
                                    {
                                        RIO_BUF sendBuffer = new RIO_BUF { BufferId = _rioBufferId, Offset = conn->SendOffset, Length = result.BytesTransferred };

                                        fixed (byte* pGiantBuffer = _giantPinnedBuffer)
                                        {
                                            Buffer.MemoryCopy(
                                                pGiantBuffer + conn->RecvOffset,
                                                pGiantBuffer + conn->SendOffset,
                                                BUFFER_SIZE,
                                                result.BytesTransferred);
                                        }

                                        RIOSend(conn->RequestQueue, ref sendBuffer, 1, 0, 1);
                                    }
                                    else // SEND Completed
                                    {
                                        RIO_BUF receiveBuffer = new RIO_BUF { BufferId = _rioBufferId, Offset = conn->RecvOffset, Length = BUFFER_SIZE };
                                        RIOReceive(conn->RequestQueue, ref receiveBuffer, 1, 0, 0);
                                    }
                                }

                                while (Interlocked.CompareExchange(ref _dequeueLock, 1, 0) != 0) { Thread.SpinWait(1); }
                                completedCount = RIODequeueCompletion(_rioCompletionQueue, pLocalResults, maxResults);
                                Volatile.Write(ref _dequeueLock, 0);
                            }
                        }
                    }
                }
            }
        }
    }
}
