using System.Diagnostics;
using System.Threading.Channels;

namespace StorageGO;

/// <summary>
/// A bounded, thread-safe ISmbClient facade for one immutable endpoint/share/identity.
/// Each operation leases a client; opened streams retain their lease until disposal.
/// </summary>
public sealed class PooledSmbClient : ISmbClient
{
    private readonly object _sync = new();
    private readonly SmbClientOptions _clientOptions;
    private readonly SmbClientPoolOptions _options;
    private readonly Channel<Slot> _available;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationToken _shutdownToken;
    private readonly Task _sweeper;
    private readonly TaskCompletionSource _shutdownComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;
    private int _active, _pending, _idle;
    private long _created, _discarded, _timeouts, _rejected, _cleanupFailures;

    private PooledSmbClient(SmbClientOptions clientOptions, SmbClientPoolOptions options)
    {
        ArgumentNullException.ThrowIfNull(clientOptions);
        SmbClient.Validate(clientOptions);
        options.Validate();
        _clientOptions = clientOptions;
        _options = options;
        _shutdownToken = _shutdown.Token;
        _available = Channel.CreateBounded<Slot>(new BoundedChannelOptions(options.MaxClients)
        {
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        // Slots, not connections: network authentication remains lazy, on first file I/O.
        for (var index = 0; index < options.MaxClients; index++) _available.Writer.TryWrite(new Slot());
        _sweeper = SweepIdleAsync();
    }

    public static PooledSmbClient Create(SmbClientOptions clientOptions, SmbClientPoolOptions? poolOptions = null) =>
        new(clientOptions, poolOptions ?? new SmbClientPoolOptions());

    public SmbClientPoolStatistics GetStatistics()
    {
        lock (_sync)
            return new(_active, _pending, _idle, _created, _discarded, _timeouts, _rejected, _cleanupFailures);
    }

    public Stream OpenRead(string remotePath) => Open(client => client.OpenRead(remotePath));
    public Stream OpenWrite(string remotePath, SmbWriteOptions? options = null) => Open(client => client.OpenWrite(remotePath, options));
    public SmbFile Read(string remotePath) => Execute(client => client.Read(remotePath));
    public SmbWriteResult CopyTo(string remotePath, Stream destination, CancellationToken cancellationToken = default) =>
        Execute(client => client.CopyTo(remotePath, destination, cancellationToken), cancellationToken);
    public SmbWriteResult Write(string remotePath, byte[] source, SmbWriteOptions? options = null) =>
        Execute(client => client.Write(remotePath, source, options));
    public SmbWriteResult Write(string remotePath, ReadOnlyMemory<byte> content, SmbWriteOptions? options = null) =>
        Execute(client => client.Write(remotePath, content, options));
    public SmbWriteResult Write(string remotePath, Stream source, SmbWriteOptions? options = null, CancellationToken cancellationToken = default) =>
        Execute(client => client.Write(remotePath, source, options, cancellationToken), cancellationToken);

    public Task<Stream> OpenReadAsync(string remotePath, CancellationToken cancellationToken = default) =>
        OpenAsync(client => client.OpenReadAsync(remotePath, cancellationToken), cancellationToken);
    public Task<Stream> OpenWriteAsync(string remotePath, SmbWriteOptions? options = null, CancellationToken cancellationToken = default) =>
        OpenAsync(client => client.OpenWriteAsync(remotePath, options, cancellationToken), cancellationToken);
    public Task<SmbFile> ReadAsync(string remotePath, CancellationToken cancellationToken = default) =>
        ExecuteAsync(client => client.ReadAsync(remotePath, cancellationToken), cancellationToken);
    public Task<SmbWriteResult> CopyToAsync(string remotePath, Stream destination, CancellationToken cancellationToken = default) =>
        ExecuteAsync(client => client.CopyToAsync(remotePath, destination, cancellationToken), cancellationToken);
    public Task<SmbWriteResult> WriteAsync(string remotePath, byte[] source, SmbWriteOptions? options = null, CancellationToken cancellationToken = default) =>
        ExecuteAsync(client => client.WriteAsync(remotePath, source, options, cancellationToken), cancellationToken);
    public Task<SmbWriteResult> WriteAsync(string remotePath, ReadOnlyMemory<byte> content, SmbWriteOptions? options = null, CancellationToken cancellationToken = default) =>
        ExecuteAsync(client => client.WriteAsync(remotePath, content, options, cancellationToken), cancellationToken);
    public Task<SmbWriteResult> WriteAsync(string remotePath, Stream source, SmbWriteOptions? options = null, CancellationToken cancellationToken = default) =>
        ExecuteAsync(client => client.WriteAsync(remotePath, source, options, cancellationToken), cancellationToken);

    private T Execute<T>(Func<ISmbClient, T> operation, CancellationToken cancellationToken = default)
    {
        using var lease = RentAsync(cancellationToken).AsTask().GetAwaiter().GetResult();
        try { return operation(lease.Client); }
        catch (Exception error) { lease.ObserveFailure(error); throw; }
    }

    private async Task<T> ExecuteAsync<T>(Func<ISmbClient, Task<T>> operation, CancellationToken cancellationToken)
    {
        await using var lease = await RentAsync(cancellationToken).ConfigureAwait(false);
        try { return await operation(lease.Client).ConfigureAwait(false); }
        catch (Exception error) { lease.ObserveFailure(error); throw; }
    }

    private Stream Open(Func<ISmbClient, Stream> operation)
    {
        var lease = RentAsync(default).AsTask().GetAwaiter().GetResult();
        try { return new PooledSmbStream(operation(lease.Client), lease); }
        catch (Exception error) { lease.ObserveFailure(error); lease.Dispose(); throw; }
    }

    private async Task<Stream> OpenAsync(Func<ISmbClient, Task<Stream>> operation, CancellationToken cancellationToken)
    {
        var lease = await RentAsync(cancellationToken).ConfigureAwait(false);
        try { return new PooledSmbStream(await operation(lease.Client).ConfigureAwait(false), lease); }
        catch (Exception error) { lease.ObserveFailure(error); await lease.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private async ValueTask<ClientLease> RentAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Slot? slot;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            slot = TakeAvailableSlot();
            if (slot is not null)
            {
                _active++;
                if (slot.Client is not null) _idle--;
            }
            else
            {
                if (_pending >= _options.MaxPendingAcquires)
                {
                    _rejected++;
                    throw new SmbClientPoolFullException();
                }
                _pending++;
            }
        }

        if (slot is null)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownToken);
            deadline.CancelAfter(_options.AcquireTimeout);
            try { slot = await _available.Reader.ReadAsync(deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                lock (_sync)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    cancellationToken.ThrowIfCancellationRequested();
                    _timeouts++;
                }
                throw new TimeoutException("Timed out waiting for an available SMB client; no SMB operation was started.");
            }
            catch (ChannelClosedException) { throw new ObjectDisposedException(nameof(PooledSmbClient)); }
            finally { lock (_sync) _pending--; }

            var stopped = false;
            lock (_sync)
            {
                stopped = _disposed;
                if (slot.Client is not null) _idle--;
                if (!stopped) _active++;
            }
            if (stopped)
            {
                await CloseSlotAsync(slot, discarded: false).ConfigureAwait(false);
                throw new ObjectDisposedException(nameof(PooledSmbClient));
            }
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (slot.Client is not null && Stopwatch.GetElapsedTime(slot.LastReturned) >= _options.IdleTimeout)
                await CloseSlotAsync(slot, discarded: true).ConfigureAwait(false);
            if (slot.Client is null)
            {
                slot.Client = SmbClient.Create(_clientOptions);
                lock (_sync) _created++;
            }
            // Shutdown may race a synchronous factory; never start a new file operation afterwards.
            lock (_sync) ObjectDisposedException.ThrowIf(_disposed, this);
            return new ClientLease(this, slot);
        }
        catch
        {
            await ReturnAsync(slot, reusable: false).ConfigureAwait(false);
            throw;
        }
    }

    // Called under _sync. Prefer a warm connection over an unused capacity slot;
    // otherwise sequential requests would needlessly initialize MaxClients clients.
    private Slot? TakeAvailableSlot()
    {
        if (!_available.Reader.TryRead(out var slot)) return null;
        if (slot.Client is not null || _idle == 0) return slot;
        var remaining = _available.Reader.Count;
        for (var index = 0; index < remaining && _available.Reader.TryRead(out var candidate); index++)
        {
            if (candidate.Client is not null)
            {
                _available.Writer.TryWrite(slot);
                return candidate;
            }
            _available.Writer.TryWrite(candidate);
        }
        return slot;
    }

    private async ValueTask ReturnAsync(Slot slot, bool reusable)
    {
        if (!reusable) await CloseSlotAsync(slot, discarded: true).ConfigureAwait(false);
        var returned = false;
        lock (_sync)
        {
            _active--;
            if (!_disposed)
            {
                slot.LastReturned = Stopwatch.GetTimestamp();
                // Readers account for idle clients after ReadAsync resumes, under the same lock.
                if (slot.Client is not null) _idle++;
                returned = _available.Writer.TryWrite(slot);
                if (!returned && slot.Client is not null) _idle--;
            }
        }
        if (!returned) await CloseSlotAsync(slot, discarded: false).ConfigureAwait(false);
    }

    private async Task SweepIdleAsync()
    {
        var interval = TimeSpan.FromMilliseconds(Math.Clamp(_options.IdleTimeout.TotalMilliseconds / 2, 10, 30_000));
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(_shutdownToken).ConfigureAwait(false))
            {
                var count = _available.Reader.Count;
                for (var index = 0; index < count; index++)
                {
                    Slot? slot;
                    lock (_sync)
                    {
                        if (_disposed || !_available.Reader.TryRead(out slot)) break;
                        if (slot.Client is not null) _idle--;
                    }
                    if (slot.Client is not null && Stopwatch.GetElapsedTime(slot.LastReturned) >= _options.IdleTimeout)
                        await CloseSlotAsync(slot, discarded: true).ConfigureAwait(false);
                    var returned = false;
                    lock (_sync)
                    {
                        if (!_disposed)
                        {
                            if (slot.Client is not null) _idle++;
                            returned = _available.Writer.TryWrite(slot);
                            if (!returned && slot.Client is not null) _idle--;
                        }
                    }
                    if (!returned) await CloseSlotAsync(slot, discarded: false).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    private async ValueTask CloseSlotAsync(Slot slot, bool discarded)
    {
        var client = slot.Client;
        slot.Client = null;
        if (client is null) return;
        if (discarded) { lock (_sync) _discarded++; }
        try { await client.DisposeAsync().ConfigureAwait(false); }
        catch { lock (_sync) _cleanupFailures++; } // Never mask the original transfer failure.
    }

    /// <summary>Stops new work and closes idle clients. Existing streams finish and close their client on return.</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public ValueTask DisposeAsync()
    {
        var first = false;
        lock (_sync)
        {
            if (!_disposed)
            {
                _disposed = true;
                first = true;
                _available.Writer.TryComplete();
            }
        }
        if (first)
        {
            _shutdown.Cancel();
            _ = ShutdownAsync();
        }
        return new ValueTask(_shutdownComplete.Task);
    }

    private async Task ShutdownAsync()
    {
        try
        {
            await _sweeper.ConfigureAwait(false);
            while (_available.Reader.TryRead(out var slot))
            {
                lock (_sync) { if (slot.Client is not null) _idle--; }
                await CloseSlotAsync(slot, discarded: false).ConfigureAwait(false);
            }
            _shutdown.Dispose();
            _shutdownComplete.TrySetResult();
        }
        catch (Exception error) { _shutdownComplete.TrySetException(error); }
    }

    internal sealed class Slot
    {
        internal ISmbClient? Client;
        internal long LastReturned;
    }

    internal sealed class ClientLease(PooledSmbClient owner, Slot slot) : IDisposable, IAsyncDisposable
    {
        private int _returned;
        private int _reusable = 1;
        internal ISmbClient Client => slot.Client!;
        internal void ObserveFailure(Exception error)
        {
            if (error is not (SmbException { ConnectionReusable: true } or ArgumentException or NotSupportedException))
                Interlocked.Exchange(ref _reusable, 0);
        }
        public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
        public ValueTask DisposeAsync() => Interlocked.Exchange(ref _returned, 1) == 0
            ? owner.ReturnAsync(slot, Volatile.Read(ref _reusable) != 0) : ValueTask.CompletedTask;
    }
}
