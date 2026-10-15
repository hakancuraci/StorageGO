using System.Buffers;

namespace StorageGO;

/// <summary>Owns only an internal scratch array, never a caller's input or result.</summary>
internal sealed class PooledBuffer(int length, bool clearOnReturn, ArrayPool<byte>? pool = null) : IDisposable
{
    private byte[]? _array = (pool ?? ArrayPool<byte>.Shared).Rent(length);
    internal byte[] Array => _array ?? throw new ObjectDisposedException(nameof(PooledBuffer));
    internal Memory<byte> Memory => Array.AsMemory(0, length);

    public void Dispose()
    {
        var array = Interlocked.Exchange(ref _array, null);
        if (array is not null) (pool ?? ArrayPool<byte>.Shared).Return(array, clearOnReturn);
    }
}
