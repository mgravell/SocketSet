# Vendored: StackExchange.Redis / RESPite.Buffers

A COPY of the CycleBuffer producer/consumer buffer from StackExchange.Redis, vendored for experimentation
on the `cyclebuffer-halfpipe` branch (see TODO.md "two half-pipes"). Not a submodule — a pinned copy so we
can hack on it freely.

- Source: https://github.com/StackExchange/StackExchange.Redis/tree/main/src/RESPite/Buffers @ main (fetched 2026-08-01)
- Files: CycleBuffer.cs, CycleBufferPool.cs, ICycleBufferCallback.cs, MemoryTrackedPool.cs
- License: MIT (StackExchange.Redis). Retain that license for these files.
- `Internal/VendorShims.cs` is OURS — minimal no-op stand-ins for the two RESPite.Internal symbols
  (DebugCounters, Experiments) the buffer files reference; everything else they need is self-contained.
