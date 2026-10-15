using System.Text.Json;

namespace StorageGO;

public sealed class SmbClient : ISmbClient
{
    private readonly object _gate = new();
    private readonly NativeClientHandle _handle;
    private readonly int _chunkSize;
    private readonly long _maxReadBytes;
    private bool _disposed;

    private SmbClient(NativeClientHandle handle, SmbClientOptions options)
    {
        _handle = handle;
        _chunkSize = options.ChunkSizeBytes;
        _maxReadBytes = Math.Min(options.MaxReadBytes, Array.MaxLength);
    }

    public static ISmbClient Create(SmbClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Validate(options);
        NativeMethods.EnsureInitialized();
        EnsureCompatibleAbi();

        var config = new
        {
            options.Server,
            options.ShareName,
            options.Username,
            options.Password,
            options.Domain,
            options.Port,
            DialTimeoutSeconds = checked((int)Math.Ceiling(options.DialTimeout.TotalSeconds)),
            options.MaxReadBytes
        };

        var response = NativeJson.Invoke<CreateResponse>(() =>
            NativeMethods.StorageGO_CreateClient(JsonSerializer.Serialize(config, NativeJson.Options)));
        NativeJson.EnsureSuccess(response.Success, response.Error);
        if (response.ClientId == 0)
        {
            throw new SmbException("Native SMB client returned an invalid client ID.");
        }

        return new SmbClient(new NativeClientHandle(response.ClientId), options);
    }

    public Stream OpenRead(string remotePath) => Open(remotePath, writeOptions: null);

    public Stream OpenWrite(string remotePath, SmbWriteOptions? options = null) =>
        Open(remotePath, options ?? new SmbWriteOptions());

    public SmbFile Read(string remotePath)
    {
        return WithStream(Open(remotePath, writeOptions: null), source =>
        {
            if (source.FileSize > _maxReadBytes)
            {
                throw ReadLimitExceeded();
            }

            using var destination = new MemoryStream(checked((int)Math.Max(0, source.FileSize)));
            var buffer = new byte[_chunkSize];
            int count;
            while ((count = source.Read(buffer, 0, buffer.Length)) != 0)
            {
                if (destination.Length > _maxReadBytes - count)
                {
                    throw ReadLimitExceeded();
                }

                destination.Write(buffer, 0, count);
            }

            return new SmbFile(source.FileName, destination.Length, source.MimeType, destination.ToArray());
        });
    }

    public SmbWriteResult CopyTo(
        string remotePath,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("The destination must be writable.", nameof(destination));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return WithStream(Open(remotePath, writeOptions: null), source =>
        {
            var buffer = new byte[_chunkSize];
            long transferred = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = source.Read(buffer, 0, buffer.Length);
                cancellationToken.ThrowIfCancellationRequested();
                if (count == 0)
                {
                    break;
                }

                destination.Write(buffer, 0, count);
                transferred = checked(transferred + count);
            }

            return new SmbWriteResult(source.FileName, transferred, source.MimeType);
        });
    }

    public SmbWriteResult Write(
        string remotePath,
        byte[] source,
        SmbWriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Write(remotePath, source.AsMemory(), options);
    }

    public SmbWriteResult Write(
        string remotePath,
        ReadOnlyMemory<byte> content,
        SmbWriteOptions? options = null)
    {
        return WithStream(Open(remotePath, options ?? new SmbWriteOptions()), destination =>
        {
            destination.Write(content.Span);
            return new SmbWriteResult(destination.FileName, destination.BytesTransferred, destination.MimeType);
        });
    }

    public SmbWriteResult Write(
        string remotePath,
        Stream source,
        SmbWriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
        {
            throw new ArgumentException("The source must be readable.", nameof(source));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return WithStream(Open(remotePath, options ?? new SmbWriteOptions()), destination =>
        {
            var buffer = new byte[_chunkSize];
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = source.Read(buffer, 0, buffer.Length);
                cancellationToken.ThrowIfCancellationRequested();
                if (count == 0)
                {
                    break;
                }

                destination.Write(buffer, 0, count);
            }

            return new SmbWriteResult(destination.FileName, destination.BytesTransferred, destination.MimeType);
        });
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            // Transfer handles retain a native reference until their streams are closed.
            _handle.Dispose();
        }
    }

    private NativeSmbStream Open(string remotePath, SmbWriteOptions? writeOptions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        if (remotePath.Contains('\0'))
        {
            throw new ArgumentException("Remote paths cannot contain a null character.", nameof(remotePath));
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var response = NativeJson.Invoke<NativeJson.OpenResponse>(() => writeOptions is null
                ? NativeMethods.StorageGO_OpenRead(_handle, remotePath)
                : NativeMethods.StorageGO_OpenWrite(_handle, remotePath, writeOptions.Overwrite ? 1 : 0));
            NativeJson.EnsureSuccess(response.Success, response.Error);
            if (response.TransferId == 0)
            {
                throw new SmbException("Native SMB layer returned an invalid transfer ID.");
            }

            NativeTransferHandle? transfer = null;
            try
            {
                transfer = new NativeTransferHandle(response.TransferId, _handle);
                return new NativeSmbStream(
                    transfer,
                    _chunkSize,
                    readable: writeOptions is null,
                    forceSync: writeOptions?.ForceSync ?? false,
                    inferMime: writeOptions is not null && string.IsNullOrEmpty(writeOptions.MimeType),
                    response.FileName ?? remotePath.Replace('\\', '/').Split('/')[^1],
                    response.FileSize,
                    !string.IsNullOrEmpty(writeOptions?.MimeType)
                        ? writeOptions.MimeType
                        : response.MimeType ?? "application/octet-stream");
            }
            catch
            {
                if (transfer is not null)
                {
                    transfer.Dispose();
                }
                else
                {
                    try
                    {
                        NativeJson.Invoke<NativeJson.StatusResponse>(() =>
                            NativeMethods.StorageGO_CloseTransfer(response.TransferId, 0));
                    }
                    catch
                    {
                        // Preserve the allocation/handle failure; native client disposal also cleans transfers.
                    }
                }

                throw;
            }
        }
    }

    private SmbException ReadLimitExceeded() => new(
        $"The file exceeds the in-memory Read limit of {_maxReadBytes} bytes. Use OpenRead or CopyTo for streaming.");

    private static T WithStream<T>(NativeSmbStream stream, Func<NativeSmbStream, T> operation)
    {
        T result;
        try
        {
            result = operation(stream);
        }
        catch
        {
            // Keep the original I/O/cancellation failure if closing a partial transfer also fails.
            stream.Abort();
            throw;
        }

        stream.Dispose();
        return result;
    }

    private static void EnsureCompatibleAbi()
    {
        try
        {
            var version = NativeMethods.StorageGO_ABIVersion();
            if (version != 2)
            {
                throw new NotSupportedException($"StorageGO native ABI 2 is required; loaded ABI {version}.");
            }
        }
        catch (EntryPointNotFoundException exception)
        {
            throw new NotSupportedException("The native StorageGO library is outdated. Deploy a library with chunk ABI 2.", exception);
        }
    }

    private static void Validate(SmbClientOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Server);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ShareName);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Username);
        ArgumentNullException.ThrowIfNull(options.Password);
        if (options.Port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(options.Port));
        }

        if (options.DialTimeout <= TimeSpan.Zero || options.DialTimeout > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(options.DialTimeout));
        }

        if (options.MaxReadBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MaxReadBytes));
        }

        if (options.ChunkSizeBytes is < 1 or > 4 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(options.ChunkSizeBytes), "Chunk size must be between 1 byte and 4 MiB.");
        }
    }

    private sealed record CreateResponse(bool Success, ulong ClientId, string? Error);
}
