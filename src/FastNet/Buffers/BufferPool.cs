using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FastNet.Buffers;

/// <summary>
/// A single page-aligned native allocation carved into fixed-size slots.
/// This is the shared buffer primitive every engine consumes:
/// <list type="bullet">
///   <item>io_uring classic recv/send take a raw pointer into a slot;</item>
///   <item>io_uring READ_FIXED/WRITE_FIXED (later) register this whole block;</item>
///   <item>RIO requires exactly this kind of pre-registered block.</item>
/// </list>
/// Native + page-aligned so the memory never moves under GC and is ready to
/// hand to the kernel for zero-copy registration without pinning churn.
///
/// Not thread-safe: the echo template drives it from a single event-loop
/// thread. When reads and writes split across loops, the freelist becomes a
/// contention point and will need an MPMC structure or per-loop pools.
/// </summary>
internal sealed unsafe class BufferPool : IDisposable
{
    private readonly nint _base;
    private readonly int[] _freeSlots;
    private int _freeCount;
    private readonly nuint _totalBytes;

    public int SlotSize { get; }
    public int SlotCount { get; }

    public BufferPool(int slotCount, int slotSize)
    {
        if (slotCount <= 0) throw new ArgumentOutOfRangeException(nameof(slotCount));
        if (slotSize <= 0) throw new ArgumentOutOfRangeException(nameof(slotSize));

        SlotCount = slotCount;
        SlotSize = slotSize;
        _totalBytes = (nuint)slotCount * (nuint)slotSize;

        // Page-aligned so the block is registration-ready (RIO / io_uring fixed).
        _base = (nint)NativeMemory.AlignedAlloc(_totalBytes, 4096);
        NativeMemory.Clear((void*)_base, _totalBytes);

        // Freelist as a stack; hand out high indices first is fine.
        _freeSlots = new int[slotCount];
        for (int i = 0; i < slotCount; i++) _freeSlots[i] = i;
        _freeCount = slotCount;
    }

    /// <summary>Base pointer of the whole block (for kernel registration).</summary>
    public byte* Base => (byte*)_base;

    /// <summary>Total size in bytes (for kernel registration).</summary>
    public nuint TotalBytes => _totalBytes;

    /// <summary>Rent a slot index, or -1 if the pool is exhausted.</summary>
    public int Rent() => _freeCount > 0 ? _freeSlots[--_freeCount] : -1;

    /// <summary>Return a previously rented slot.</summary>
    public void Return(int slot)
    {
        if ((uint)slot >= (uint)SlotCount) throw new ArgumentOutOfRangeException(nameof(slot));
        _freeSlots[_freeCount++] = slot;
    }

    /// <summary>Raw pointer to the start of a slot's memory.</summary>
    public byte* PointerFor(int slot) => (byte*)_base + (nuint)slot * (nuint)SlotSize;

    /// <summary>Span for the specified slot.</summary>
    public Span<byte> SpanFor(int slot) => new(PointerFor(slot), SlotSize);

    public void Dispose()
    {
        // prevent double-dispose (we can't fix use-after-free, though)
        var oldValue = Interlocked.Exchange(ref Unsafe.AsRef(in _base), 0);
        if (oldValue != 0)
        {
            NativeMemory.AlignedFree((byte*)oldValue);
        }
    }
}
