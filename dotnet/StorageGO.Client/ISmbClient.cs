namespace StorageGO;

/// <summary>Reads and writes files on the SMB share configured for this client.</summary>
public interface ISmbClient : IDisposable, IAsyncDisposable
{
    /// <summary>Opens a forward-only stream. Dispose the stream after use.</summary>
    /// <remarks>
    /// An open stream keeps its native client alive, even after the client is disposed.
    /// Seeking, Length and Position are not supported.
    /// </remarks>
    Stream OpenRead(string remotePath);

    /// <summary>Opens a forward-only upload stream. Dispose it to close the remote file.</summary>
    /// <remarks>
    /// Writes are immediately visible and are not atomic. Failure can leave a partial file.
    /// Seeking, Length and Position are not supported. Flush syncs the remote file;
    /// ForceSync also syncs on successful disposal. Open streams keep their native client alive.
    /// </remarks>
    Stream OpenWrite(string remotePath, SmbWriteOptions? options = null);

    /// <summary>Reads a complete file into memory, subject to MaxReadBytes.</summary>
    SmbFile Read(string remotePath);

    /// <summary>Copies a remote file in bounded chunks without closing the destination.</summary>
    /// <remarks>Cancellation is checked between synchronous I/O calls.</remarks>
    SmbWriteResult CopyTo(
        string remotePath,
        Stream destination,
        CancellationToken cancellationToken = default);

    /// <summary>Uploads a byte array in bounded binary chunks.</summary>
    SmbWriteResult Write(
        string remotePath,
        byte[] source,
        SmbWriteOptions? options = null);

    SmbWriteResult Write(
        string remotePath,
        ReadOnlyMemory<byte> content,
        SmbWriteOptions? options = null);

    /// <summary>Uploads a stream in bounded chunks without closing the source.</summary>
    /// <remarks>Cancellation is checked between synchronous I/O calls. Failure can leave a partial file.</remarks>
    SmbWriteResult Write(
        string remotePath,
        Stream source,
        SmbWriteOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Opens a remote read stream using native asynchronous I/O.</summary>
    Task<Stream> OpenReadAsync(string remotePath, CancellationToken cancellationToken = default);

    /// <summary>Opens a remote upload stream using native asynchronous I/O.</summary>
    Task<Stream> OpenWriteAsync(string remotePath, SmbWriteOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Reads a complete file into memory asynchronously, subject to MaxReadBytes.</summary>
    Task<SmbFile> ReadAsync(string remotePath, CancellationToken cancellationToken = default);

    /// <summary>Copies a remote file asynchronously without closing the destination.</summary>
    /// <remarks>Cancellation of active native I/O completes after that I/O settles; partial transfers are possible.</remarks>
    Task<SmbWriteResult> CopyToAsync(string remotePath, Stream destination,
        CancellationToken cancellationToken = default);

    Task<SmbWriteResult> WriteAsync(string remotePath, byte[] source, SmbWriteOptions? options = null,
        CancellationToken cancellationToken = default);

    Task<SmbWriteResult> WriteAsync(string remotePath, ReadOnlyMemory<byte> content, SmbWriteOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Uploads a stream asynchronously without closing the source.</summary>
    /// <remarks>Cancellation of active native I/O completes after that I/O settles; partial files are possible.</remarks>
    Task<SmbWriteResult> WriteAsync(string remotePath, Stream source, SmbWriteOptions? options = null,
        CancellationToken cancellationToken = default);
}
