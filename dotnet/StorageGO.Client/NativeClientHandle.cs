using Microsoft.Win32.SafeHandles;

namespace StorageGO;

internal sealed class NativeClientHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _transfers;

    internal NativeClientHandle(ulong clientId) : base(ownsHandle: true) =>
        SetHandle(unchecked((IntPtr)(long)clientId));

    internal ulong Id => unchecked((ulong)handle.ToInt64());
    internal void TransferOpened() => Interlocked.Increment(ref _transfers);
    internal void TransferClosed() => Interlocked.Decrement(ref _transfers);
    internal Task DeferredCloseCompletion => IsClosed && Volatile.Read(ref _transfers) == 0
        ? _closed.Task : Task.CompletedTask;

    internal void DisposeChecked()
    {
        var deferred = Volatile.Read(ref _transfers) != 0;
        Dispose();
        if (!deferred)
        {
            _closed.Task.GetAwaiter().GetResult();
        }
    }

    internal async ValueTask DisposeCheckedAsync()
    {
        var deferred = Volatile.Read(ref _transfers) != 0;
        Dispose();
        if (!deferred)
        {
            await _closed.Task.ConfigureAwait(false);
        }
    }

    protected override bool ReleaseHandle()
    {
        // SafeHandle release can run on a callback/finalizer thread. Never perform blocking SMB I/O here.
        _ = CloseNativeAsync(Id);
        return true;
    }

    private async Task CloseNativeAsync(ulong clientId)
    {
        try
        {
            var response = await NativeAsync.Invoke<NativeJson.StatusResponse>(
                new("closeClient", ClientId: clientId)).ConfigureAwait(false);
            NativeJson.EnsureSuccess(response.Success, response.Error);
            _closed.TrySetResult();
        }
        catch (Exception exception)
        {
            try
            {
                // Guaranteed admission-independent cleanup if the regular operation queue is full.
                NativeMethods.StorageGO_ReleaseClient(clientId);
            }
            catch
            {
                // Finalizers cannot throw. Explicit disposal still receives the original failure.
            }

            _closed.TrySetException(exception);
        }
    }
}
