namespace StorageGO;

/// <summary>A synchronous SMB stream backed by bounded binary native calls.</summary>
internal sealed class NativeSmbStream : Stream
{
    private readonly object _gate = new();
    private readonly NativeTransferHandle _handle;
    private readonly int _chunkSize;
    private readonly bool _readable;
    private readonly bool _forceSync;
    private readonly byte[]? _mimePrefix;
    private int _mimePrefixLength;
    private byte[]? _buffer;
    private long _position;
    private bool _eof;
    private bool _faulted;
    private bool _disposed;

    internal NativeSmbStream(
        NativeTransferHandle handle,
        int chunkSize,
        bool readable,
        bool forceSync,
        bool inferMime,
        string fileName,
        long fileSize,
        string mimeType)
    {
        _handle = handle;
        _chunkSize = chunkSize;
        _readable = readable;
        _forceSync = forceSync;
        _mimePrefix = inferMime && mimeType == "application/octet-stream" ? new byte[8] : null;
        FileName = fileName;
        FileSize = fileSize;
        MimeType = mimeType;
    }

    internal string FileName { get; }
    internal long FileSize { get; }
    internal string MimeType { get; private set; }
    internal long BytesTransferred { get { lock (_gate) { return _position; } } }

    public override bool CanRead => !_disposed && _readable;
    public override bool CanSeek => false;
    public override bool CanWrite => !_disposed && !_readable;
    public override long Length => throw new NotSupportedException("SMB streams are forward-only.");
    public override long Position
    {
        get => throw new NotSupportedException("SMB streams are forward-only.");
        set => throw new NotSupportedException("SMB streams are forward-only.");
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBuffer(buffer, offset, count);
        lock (_gate)
        {
            EnsureReadable();
            if (count == 0 || _eof)
            {
                return 0;
            }

            var capacity = Math.Min(count, _chunkSize);
            if (offset == 0)
            {
                return ReadChunk(buffer, capacity);
            }

            var transferred = ReadChunk(Buffer, capacity);
            Buffer.AsSpan(0, transferred).CopyTo(buffer.AsSpan(offset, transferred));
            return transferred;
        }
    }

    public override int Read(Span<byte> buffer)
    {
        lock (_gate)
        {
            EnsureReadable();
            if (buffer.IsEmpty || _eof)
            {
                return 0;
            }

            var transferred = ReadChunk(Buffer, Math.Min(buffer.Length, _chunkSize));
            Buffer.AsSpan(0, transferred).CopyTo(buffer);
            return transferred;
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBuffer(buffer, offset, count);
        lock (_gate)
        {
            EnsureWritable();
            WriteCore(buffer.AsSpan(offset, count));
        }
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        lock (_gate)
        {
            EnsureWritable();
            WriteCore(buffer);
        }
    }

    public override void Flush()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureNotFaulted();
            if (_readable)
            {
                return;
            }

            try
            {
                var response = NativeJson.Invoke<NativeJson.StatusResponse>(() =>
                    NativeMethods.StorageGO_FlushTransfer(_handle));
                NativeJson.EnsureSuccess(response.Success, response.Error);
            }
            catch
            {
                _faulted = true;
                throw;
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _buffer = null;
            try
            {
                if (disposing)
                {
                    _handle.CloseChecked(_forceSync && !_readable && !_faulted);
                }
            }
            catch when (_faulted)
            {
                // The caller already received the transfer failure. Do not replace it with cleanup failure.
            }
            finally
            {
                base.Dispose(disposing);
            }
        }
    }

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException("SMB streams are forward-only.");

    public override void SetLength(long value) =>
        throw new NotSupportedException("SMB streams are forward-only.");

    // There is no asynchronous native ABI. Avoid silently using the thread pool for blocking SMB I/O.
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        throw AsyncNotSupported();

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        throw AsyncNotSupported();

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        throw AsyncNotSupported();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        throw AsyncNotSupported();

    public override Task FlushAsync(CancellationToken cancellationToken) => throw AsyncNotSupported();

    public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken) =>
        throw AsyncNotSupported();

    private byte[] Buffer => _buffer ??= new byte[_chunkSize];

    internal void Abort()
    {
        lock (_gate)
        {
            _faulted = true;
            Dispose();
        }
    }

    private int ReadChunk(byte[] buffer, int capacity)
    {
        try
        {
            var response = NativeJson.Invoke<NativeJson.ChunkResponse>(() =>
                NativeMethods.StorageGO_ReadChunk(_handle, buffer, checked((uint)capacity)));
            NativeJson.EnsureSuccess(response.Success, response.Error);
            if (response.BytesTransferred < 0 || response.BytesTransferred > capacity ||
                (response.BytesTransferred == 0 && !response.Eof))
            {
                throw new SmbException("Native SMB read returned an invalid byte count.");
            }

            _eof = response.Eof;
            _position = checked(_position + response.BytesTransferred);
            return response.BytesTransferred;
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    private void WriteCore(ReadOnlySpan<byte> source)
    {
        try
        {
            while (!source.IsEmpty)
            {
                var count = Math.Min(source.Length, _chunkSize);
                source[..count].CopyTo(Buffer);
                var response = NativeJson.Invoke<NativeJson.ChunkResponse>(() =>
                    NativeMethods.StorageGO_WriteChunk(_handle, Buffer, checked((uint)count)));
                NativeJson.EnsureSuccess(response.Success, response.Error);
                if (response.BytesTransferred != count)
                {
                    throw new SmbException("Native SMB write returned an invalid byte count.");
                }

                _position = checked(_position + response.BytesTransferred);
                ObserveMimePrefix(source[..response.BytesTransferred]);
                source = source[response.BytesTransferred..];
            }
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    private void EnsureReadable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureNotFaulted();
        if (!_readable)
        {
            throw new NotSupportedException("This SMB stream is write-only.");
        }
    }

    private void EnsureWritable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureNotFaulted();
        if (_readable)
        {
            throw new NotSupportedException("This SMB stream is read-only.");
        }
    }

    private static void ValidateBuffer(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count)
        {
            throw new ArgumentException("Offset and count exceed the buffer length.");
        }
    }

    private static NotSupportedException AsyncNotSupported() =>
        new("StorageGO streams support synchronous I/O only. Use Read, Write, Flush or ISmbClient.CopyTo.");

    private void EnsureNotFaulted()
    {
        if (_faulted)
        {
            throw new SmbException("The SMB stream failed during an earlier operation. Dispose it and open a new transfer.");
        }
    }

    private void ObserveMimePrefix(ReadOnlySpan<byte> source)
    {
        if (_mimePrefix is null || _mimePrefixLength == _mimePrefix.Length || MimeType != "application/octet-stream")
        {
            return;
        }

        var count = Math.Min(source.Length, _mimePrefix.Length - _mimePrefixLength);
        source[..count].CopyTo(_mimePrefix.AsSpan(_mimePrefixLength));
        _mimePrefixLength += count;
        var prefix = _mimePrefix.AsSpan(0, _mimePrefixLength);
        if (prefix.StartsWith("%PDF"u8))
        {
            MimeType = "application/pdf";
        }
        else if (prefix.Length == 8 && prefix[0] == 0x89 && prefix[1..].SequenceEqual("PNG\r\n\u001a\n"u8))
        {
            MimeType = "image/png";
        }
        else if (prefix.Length >= 2 && prefix[0] == 0xFF && prefix[1] == 0xD8)
        {
            MimeType = "image/jpeg";
        }
    }
}
