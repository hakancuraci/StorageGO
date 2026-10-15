namespace StorageGO;

/// <summary>Holds an exclusive pool lease until both I/O and native stream disposal have settled.</summary>
internal sealed class PooledSmbStream(Stream inner, PooledSmbClient.ClientLease lease) : Stream
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public override bool CanRead => !Volatile.Read(ref _disposed) && inner.CanRead;
    public override bool CanWrite => !Volatile.Read(ref _disposed) && inner.CanWrite;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException("SMB streams are forward-only.");
    public override long Position
    {
        get => throw new NotSupportedException("SMB streams are forward-only.");
        set => throw new NotSupportedException("SMB streams are forward-only.");
    }

    private void EnsureOpen() => ObjectDisposedException.ThrowIf(_disposed, this);

    private T Invoke<T>(Func<T> operation)
    {
        _gate.Wait();
        try
        {
            EnsureOpen();
            try { return operation(); }
            catch (Exception error) { lease.ObserveFailure(error); throw; }
        }
        finally { _gate.Release(); }
    }

    private void Invoke(Action operation) => Invoke(() => { operation(); return 0; });

    public override int Read(byte[] buffer, int offset, int count) => Invoke(() => inner.Read(buffer, offset, count));
    public override void Write(byte[] buffer, int offset, int count) => Invoke(() => inner.Write(buffer, offset, count));

    public override int Read(Span<byte> buffer)
    {
        _gate.Wait();
        try
        {
            EnsureOpen();
            try { return inner.Read(buffer); }
            catch (Exception error) { lease.ObserveFailure(error); throw; }
        }
        finally { _gate.Release(); }
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _gate.Wait();
        try
        {
            EnsureOpen();
            try { inner.Write(buffer); }
            catch (Exception error) { lease.ObserveFailure(error); throw; }
        }
        finally { _gate.Release(); }
    }

    public override void Flush() => Invoke(inner.Flush);
    public override void CopyTo(Stream destination, int bufferSize) => Invoke(() => inner.CopyTo(destination, bufferSize));
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException("SMB streams are forward-only.");
    public override void SetLength(long value) => throw new NotSupportedException("SMB streams are forward-only.");

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureOpen();
            try { return await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) { lease.ObserveFailure(error); throw; }
        }
        finally { _gate.Release(); }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureOpen();
            try { await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) { lease.ObserveFailure(error); throw; }
        }
        finally { _gate.Release(); }
    }

    private async Task InvokeAsync(Func<Task> operation, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureOpen();
            try { await operation().ConfigureAwait(false); }
            catch (Exception error) { lease.ObserveFailure(error); throw; }
        }
        finally { _gate.Release(); }
    }

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        InvokeAsync(() => inner.FlushAsync(cancellationToken), cancellationToken);
    public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken) =>
        InvokeAsync(() => inner.CopyToAsync(destination, bufferSize, cancellationToken), cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (!disposing) { base.Dispose(false); return; }
        _gate.Wait();
        try
        {
            if (_disposed) return;
            _disposed = true;
            try { inner.Dispose(); }
            catch (Exception error) { lease.ObserveFailure(error); throw; }
            finally { lease.Dispose(); base.Dispose(true); }
        }
        finally { _gate.Release(); }
    }

    public override async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            try { await inner.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { lease.ObserveFailure(error); throw; }
            finally
            {
                await lease.DisposeAsync().ConfigureAwait(false);
                GC.SuppressFinalize(this);
            }
        }
        finally { _gate.Release(); }
    }
}
