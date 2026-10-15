namespace StorageGO;

public sealed partial class SmbClient
{
    public async Task<Stream> OpenReadAsync(string remotePath, CancellationToken cancellationToken = default) =>
        await OpenAsync(remotePath, null, cancellationToken).ConfigureAwait(false);

    public async Task<Stream> OpenWriteAsync(string remotePath, SmbWriteOptions? options = null,
        CancellationToken cancellationToken = default) =>
        await OpenAsync(remotePath, options ?? new SmbWriteOptions(), cancellationToken).ConfigureAwait(false);

    public async Task<SmbFile> ReadAsync(string remotePath, CancellationToken cancellationToken = default)
    {
        var stream = await OpenAsync(remotePath, null, cancellationToken).ConfigureAwait(false);
        return await WithStreamAsync(stream, async source =>
        {
            if (source.FileSize > _maxReadBytes) throw ReadLimitExceeded();
            using var destination = new MemoryStream(checked((int)Math.Max(0, source.FileSize)));
            var buffer = new byte[_chunkSize];
            int count;
            while ((count = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
            {
                if (destination.Length > _maxReadBytes - count) throw ReadLimitExceeded();
                await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            }

            return new SmbFile(source.FileName, destination.Length, source.MimeType, destination.ToArray());
        }).ConfigureAwait(false);
    }

    public async Task<SmbWriteResult> CopyToAsync(string remotePath, Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite) throw new ArgumentException("The destination must be writable.", nameof(destination));
        var stream = await OpenAsync(remotePath, null, cancellationToken).ConfigureAwait(false);
        return await WithStreamAsync(stream, async source =>
        {
            var buffer = new byte[_chunkSize];
            long transferred = 0;
            int count;
            while ((count = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                transferred = checked(transferred + count);
            }

            return new SmbWriteResult(source.FileName, transferred, source.MimeType);
        }).ConfigureAwait(false);
    }

    public Task<SmbWriteResult> WriteAsync(string remotePath, byte[] source, SmbWriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        return WriteAsync(remotePath, source.AsMemory(), options, cancellationToken);
    }

    public async Task<SmbWriteResult> WriteAsync(string remotePath, ReadOnlyMemory<byte> content,
        SmbWriteOptions? options = null, CancellationToken cancellationToken = default)
    {
        var stream = await OpenAsync(remotePath, options ?? new SmbWriteOptions(), cancellationToken).ConfigureAwait(false);
        return await WithStreamAsync(stream, async destination =>
        {
            await destination.WriteAsync(content, cancellationToken).ConfigureAwait(false);
            return new SmbWriteResult(destination.FileName, destination.BytesTransferred, destination.MimeType);
        }).ConfigureAwait(false);
    }

    public async Task<SmbWriteResult> WriteAsync(string remotePath, Stream source, SmbWriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead) throw new ArgumentException("The source must be readable.", nameof(source));
        var stream = await OpenAsync(remotePath, options ?? new SmbWriteOptions(), cancellationToken).ConfigureAwait(false);
        return await WithStreamAsync(stream, async destination =>
        {
            var buffer = new byte[_chunkSize];
            while (true)
            {
                var count = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (count == 0) break;
                await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            }

            return new SmbWriteResult(destination.FileName, destination.BytesTransferred, destination.MimeType);
        }).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            await _handle.DisposeCheckedAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<NativeSmbStream> OpenAsync(string remotePath, SmbWriteOptions? options,
        CancellationToken cancellationToken)
    {
        ValidatePath(remotePath);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var response = await NativeAsync.Invoke<NativeJson.OpenResponse>(
                new(options is null ? "openRead" : "openWrite", ClientId: _handle.Id,
                    Path: remotePath, Overwrite: options?.Overwrite ?? false),
                _handle, cancellationToken: cancellationToken).ConfigureAwait(false);
            NativeJson.EnsureSuccess(response.Success, response.Error);
            // Keep the client gate until the transfer has acquired its independent native client reference.
            return CreateStream(response, remotePath, options);
        }
        finally { _gate.Release(); }
    }

    private static async Task<T> WithStreamAsync<T>(NativeSmbStream stream, Func<NativeSmbStream, Task<T>> operation)
    {
        T result;
        try { result = await operation(stream).ConfigureAwait(false); }
        catch
        {
            try { await stream.AbortAsync().ConfigureAwait(false); }
            catch { /* Preserve the original I/O/cancellation failure. */ }
            throw;
        }

        await stream.DisposeAsync().ConfigureAwait(false);
        return result;
    }
}
