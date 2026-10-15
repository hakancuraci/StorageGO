namespace StorageGO;

/// <summary>Reads and writes files on the SMB share configured for this client.</summary>
public interface ISmbClient : IDisposable
{
    /// <summary>Opens a synchronous, forward-only stream. Dispose the stream after use.</summary>
    /// <remarks>
    /// An open stream keeps its native client alive, even after the client is disposed.
    /// Seeking, Length, Position and asynchronous I/O are not supported.
    /// </remarks>
    Stream OpenRead(string remotePath);

    /// <summary>Opens a synchronous, forward-only upload stream. Dispose it to close the remote file.</summary>
    /// <remarks>
    /// Writes are immediately visible and are not atomic. Failure can leave a partial file.
    /// Seeking, Length, Position and asynchronous I/O are not supported. Flush syncs the remote file;
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
}
