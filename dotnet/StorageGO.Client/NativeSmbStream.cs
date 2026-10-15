namespace StorageGO;

/// <summary>A forward-only SMB stream with bounded binary synchronous and callback-based asynchronous I/O.</summary>
internal sealed class NativeSmbStream : Stream
{
    private readonly SemaphoreSlim _gate = new(1, 1);
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

    internal NativeSmbStream(NativeTransferHandle handle, int chunkSize, bool readable, bool forceSync,
        bool inferMime, string fileName, long fileSize, string mimeType)
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
    internal long BytesTransferred => Interlocked.Read(ref _position);
    public override bool CanRead => !_disposed && _readable;
    public override bool CanSeek => false;
    public override bool CanWrite => !_disposed && !_readable;
    public override long Length => throw ForwardOnly();
    public override long Position { get => throw ForwardOnly(); set => throw ForwardOnly(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBuffer(buffer, offset, count);
        _gate.Wait();
        try
        {
            EnsureReadable();
            if (count == 0 || _eof) return 0;
            var capacity = Math.Min(count, _chunkSize);
            if (offset == 0) return ReadChunk(buffer, capacity);
            var read = ReadChunk(Buffer, capacity);
            Buffer.AsSpan(0, read).CopyTo(buffer.AsSpan(offset, read));
            return read;
        }
        finally { _gate.Release(); }
    }

    public override int Read(Span<byte> buffer)
    {
        _gate.Wait();
        try
        {
            EnsureReadable();
            if (buffer.IsEmpty || _eof) return 0;
            var read = ReadChunk(Buffer, Math.Min(buffer.Length, _chunkSize));
            Buffer.AsSpan(0, read).CopyTo(buffer);
            return read;
        }
        finally { _gate.Release(); }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBuffer(buffer, offset, count);
        Write(buffer.AsSpan(offset, count));
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _gate.Wait();
        try { EnsureWritable(); WriteCore(buffer); }
        finally { _gate.Release(); }
    }

    public override void Flush()
    {
        _gate.Wait();
        try
        {
            EnsureUsable();
            if (_readable) return;
            try
            {
                var response = NativeJson.Invoke<NativeJson.StatusResponse>(() => NativeMethods.StorageGO_FlushTransfer(_handle));
                NativeJson.EnsureSuccess(response.Success, response.Error);
            }
            catch { _faulted = true; throw; }
        }
        finally { _gate.Release(); }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBuffer(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureReadable();
            cancellationToken.ThrowIfCancellationRequested();
            if (buffer.IsEmpty || _eof) return 0;
            var read = await ReadChunkAsync(Math.Min(buffer.Length, _chunkSize), cancellationToken).ConfigureAwait(false);
            Buffer.AsMemory(0, read).CopyTo(buffer);
            return read;
        }
        finally { _gate.Release(); }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBuffer(buffer, offset, count);
        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureWritable();
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                while (!buffer.IsEmpty)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = Math.Min(buffer.Length, _chunkSize);
                    buffer[..count].CopyTo(Buffer);
                    var response = await NativeAsync.Invoke<NativeJson.ChunkResponse>(
                        new("write", TransferId: _handle.Id), _handle,
                        writeBuffer: Buffer, count: count, cancellationToken: cancellationToken).ConfigureAwait(false);
                    ValidateWrite(response, count);
                    ObserveMimePrefix(Buffer.AsSpan(0, count));
                    _position = checked(_position + count);
                    buffer = buffer[count..];
                }
            }
            catch { _faulted = true; throw; }
        }
        finally { _gate.Release(); }
    }

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureUsable();
            cancellationToken.ThrowIfCancellationRequested();
            if (_readable) return;
            try
            {
                var response = await NativeAsync.Invoke<NativeJson.StatusResponse>(
                    new("flush", TransferId: _handle.Id), _handle, cancellationToken: cancellationToken).ConfigureAwait(false);
                NativeJson.EnsureSuccess(response.Success, response.Error);
            }
            catch { _faulted = true; throw; }
        }
        finally { _gate.Release(); }
    }

    public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferSize);
        if (!CanRead) { EnsureReadable(); }
        if (!destination.CanWrite) throw new ArgumentException("The destination must be writable.", nameof(destination));
        var buffer = new byte[Math.Min(bufferSize, _chunkSize)];
        int read;
        while ((read = await ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing) { base.Dispose(false); return; }
        _gate.Wait();
        try
        {
            if (_disposed) return;
            _disposed = true;
            _buffer = null;
            try { _handle.CloseChecked(_forceSync && !_readable && !_faulted); }
            catch when (_faulted) { /* Preserve an earlier transfer failure during cleanup. */ }
            finally { base.Dispose(true); }
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
            _buffer = null;
            try { await _handle.CloseCheckedAsync(_forceSync && !_readable && !_faulted).ConfigureAwait(false); }
            catch when (_faulted) { /* Preserve an earlier transfer failure during cleanup. */ }
            finally { GC.SuppressFinalize(this); }
        }
        finally { _gate.Release(); }
    }

    internal void Abort()
    {
        _gate.Wait();
        try { _faulted = true; }
        finally { _gate.Release(); }
        Dispose();
    }

    internal async ValueTask AbortAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { _faulted = true; }
        finally { _gate.Release(); }
        await DisposeAsync().ConfigureAwait(false);
    }

    public override long Seek(long offset, SeekOrigin origin) => throw ForwardOnly();
    public override void SetLength(long value) => throw ForwardOnly();
    private byte[] Buffer => _buffer ??= new byte[_chunkSize];

    private int ReadChunk(byte[] buffer, int capacity)
    {
        try
        {
            var response = NativeJson.Invoke<NativeJson.ChunkResponse>(() =>
                NativeMethods.StorageGO_ReadChunk(_handle, buffer, checked((uint)capacity)));
            return AcceptRead(response, capacity);
        }
        catch { _faulted = true; throw; }
    }

    private async Task<int> ReadChunkAsync(int capacity, CancellationToken cancellationToken)
    {
        try
        {
            var response = await NativeAsync.Invoke<NativeJson.ChunkResponse>(
                new("read", TransferId: _handle.Id), _handle, count: capacity,
                readBuffer: Buffer, cancellationToken: cancellationToken).ConfigureAwait(false);
            return AcceptRead(response, capacity);
        }
        catch { _faulted = true; throw; }
    }

    private int AcceptRead(NativeJson.ChunkResponse response, int capacity)
    {
        NativeJson.EnsureSuccess(response.Success, response.Error);
        if (response.BytesTransferred < 0 || response.BytesTransferred > capacity || (response.BytesTransferred == 0 && !response.Eof))
            throw new SmbException("Native SMB read returned an invalid byte count.");
        _eof = response.Eof;
        _position = checked(_position + response.BytesTransferred);
        return response.BytesTransferred;
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
                ValidateWrite(response, count);
                ObserveMimePrefix(source[..count]);
                _position = checked(_position + count);
                source = source[count..];
            }
        }
        catch { _faulted = true; throw; }
    }

    private static void ValidateWrite(NativeJson.ChunkResponse response, int count)
    {
        NativeJson.EnsureSuccess(response.Success, response.Error);
        if (response.BytesTransferred != count) throw new SmbException("Native SMB write returned an invalid byte count.");
    }

    private void EnsureUsable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_faulted) throw new SmbException("The SMB stream failed during an earlier operation. Dispose it and open a new transfer.");
    }

    private void EnsureReadable()
    {
        EnsureUsable();
        if (!_readable) throw new NotSupportedException("This SMB stream is write-only.");
    }

    private void EnsureWritable()
    {
        EnsureUsable();
        if (_readable) throw new NotSupportedException("This SMB stream is read-only.");
    }

    private static void ValidateBuffer(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count) throw new ArgumentException("Offset and count exceed the buffer length.");
    }

    private static NotSupportedException ForwardOnly() => new("SMB streams are forward-only.");

    private void ObserveMimePrefix(ReadOnlySpan<byte> source)
    {
        if (_mimePrefix is null || _mimePrefixLength == _mimePrefix.Length || MimeType != "application/octet-stream") return;
        var count = Math.Min(source.Length, _mimePrefix.Length - _mimePrefixLength);
        source[..count].CopyTo(_mimePrefix.AsSpan(_mimePrefixLength));
        _mimePrefixLength += count;
        var prefix = _mimePrefix.AsSpan(0, _mimePrefixLength);
        if (prefix.StartsWith("%PDF"u8)) MimeType = "application/pdf";
        else if (prefix.Length == 8 && prefix[0] == 0x89 && prefix[1..].SequenceEqual("PNG\r\n\u001a\n"u8)) MimeType = "image/png";
        else if (prefix.Length >= 2 && prefix[0] == 0xFF && prefix[1] == 0xD8) MimeType = "image/jpeg";
    }
}
