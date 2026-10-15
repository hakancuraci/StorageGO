using Microsoft.Win32.SafeHandles;

namespace StorageGO;

internal sealed class NativeTransferHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private readonly NativeClientHandle _client;
    private bool _clientReferenceHeld;

    internal NativeTransferHandle(ulong transferId, NativeClientHandle client) : base(ownsHandle: true)
    {
        _client = client;
        _client.DangerousAddRef(ref _clientReferenceHeld);
        _client.TransferOpened();
        SetHandle(unchecked((IntPtr)(long)transferId));
    }

    internal ulong Id => unchecked((ulong)handle.ToInt64());

    internal void CloseChecked(bool forceSync)
    {
        var closeFailed = false;
        try
        {
            var response = NativeJson.Invoke<NativeJson.StatusResponse>(() =>
                NativeMethods.StorageGO_CloseTransfer(Id, forceSync ? 1 : 0));
            NativeJson.EnsureSuccess(response.Success, response.Error);
        }
        catch { closeFailed = true; throw; }
        finally
        {
            CompleteClose();
            try { _client.DeferredCloseCompletion.GetAwaiter().GetResult(); }
            catch when (closeFailed) { /* Preserve the transfer close failure. */ }
        }
    }

    internal async ValueTask CloseCheckedAsync(bool forceSync)
    {
        var closeFailed = false;
        try
        {
            var response = await NativeAsync.Invoke<NativeJson.StatusResponse>(
                new("closeTransfer", TransferId: Id, ForceSync: forceSync), handle: this).ConfigureAwait(false);
            NativeJson.EnsureSuccess(response.Success, response.Error);
        }
        catch
        {
            closeFailed = true;
            try { NativeMethods.StorageGO_ReleaseTransfer(Id); }
            catch { /* Preserve the original operation failure. */ }
            throw;
        }
        finally
        {
            CompleteClose();
            try { await _client.DeferredCloseCompletion.ConfigureAwait(false); }
            catch when (closeFailed) { /* Preserve the transfer close failure. */ }
        }
    }

    private void CompleteClose()
    {
        SetHandleAsInvalid();
        ReleaseClientReference();
        Dispose();
    }

    protected override bool ReleaseHandle()
    {
        // Finalizers schedule cleanup, retaining the client until the transfer callback settles.
        _ = CloseFinalizedAsync(Id);
        return true;
    }

    private async Task CloseFinalizedAsync(ulong transferId)
    {
        try
        {
            await NativeAsync.Invoke<NativeJson.StatusResponse>(new("closeTransfer", TransferId: transferId))
                .ConfigureAwait(false);
        }
        catch
        {
            try { NativeMethods.StorageGO_ReleaseTransfer(transferId); }
            catch { /* Finalizer cleanup must never throw. */ }
        }
        finally
        {
            ReleaseClientReference();
        }
    }

    private void ReleaseClientReference()
    {
        if (_clientReferenceHeld)
        {
            _clientReferenceHeld = false;
            _client.TransferClosed();
            _client.DangerousRelease();
        }
    }
}
