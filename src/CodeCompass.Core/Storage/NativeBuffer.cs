using System.Numerics;
using System.Runtime.InteropServices;

namespace CodeCompass.Core.Storage;

/// <summary>
/// A growable buffer in native memory, for the build's per-file bytes and text. A fresh managed array per file is a
/// large object (85 KB and up), and large objects are freed only by a gen2 GC: between full GCs the dead copies piled
/// up to several GB on a big share, far past the read budget. This buffer is reused while it holds at most
/// <c>keep</c> elements, and freed as soon as it's trimmed past that - so a big file's memory goes back right after
/// the file, not whenever the GC next runs a full collection. NOT thread-safe: one per worker.
/// </summary>
internal sealed unsafe class NativeBuffer<T> : IDisposable where T : unmanaged
{
    private readonly int _keep;
    private T* _p;
    private int _capacity;

    public NativeBuffer(int keep) => _keep = keep;

    /// <summary>Elements held now (for the trim test).</summary>
    public int Capacity => _capacity;

    /// <summary>A span of <paramref name="length"/> elements (contents undefined), valid until the next Get, Trim or
    /// Dispose. Grows in powers of two up to the keep size, and to the exact length past it. Throws
    /// OutOfMemoryException when the memory can't be had, as a managed array would.</summary>
    public Span<T> Get(int length)
    {
        if (length > _capacity)
        {
            Free();
            int cap = length <= _keep ? (int)Math.Min(BitOperations.RoundUpToPowerOf2((uint)Math.Max(length, 4096)), (uint)_keep) : length;
            _p = (T*)NativeMemory.Alloc((nuint)cap, (nuint)sizeof(T));
            _capacity = cap;
        }
        return new Span<T>(_p, length);
    }

    /// <summary>Frees the buffer if it grew past the keep size (after an outlier file).</summary>
    public void Trim()
    {
        if (_capacity > _keep) Free();
    }

    public void Dispose()
    {
        Free();
        GC.SuppressFinalize(this);
    }

    ~NativeBuffer() => Free();

    private void Free()
    {
        if (_p is null) return;
        NativeMemory.Free(_p);
        _p = null;
        _capacity = 0;
    }
}
