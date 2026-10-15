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
        SetHandle(unchecked((IntPtr)(long)transferId));
    }

    internal void CloseChecked(bool forceSync)
    {
        try
        {
            var response = NativeJson.Invoke<NativeJson.StatusResponse>(() =>
                NativeMethods.StorageGO_CloseTransfer(unchecked((ulong)handle.ToInt64()), forceSync ? 1 : 0));
            NativeJson.EnsureSuccess(response.Success, response.Error);
        }
        finally
        {
            SetHandleAsInvalid();
            ReleaseClientReference();
            Dispose();
        }
    }

    protected override bool ReleaseHandle()
    {
        // Finalization is best effort. Explicit stream disposal reports close/sync failures.
        try
        {
            var response = NativeMethods.StorageGO_CloseTransfer(unchecked((ulong)handle.ToInt64()), 0);
            if (response != IntPtr.Zero)
            {
                NativeMethods.StorageGO_FreeResult(response);
            }

            return true;
        }
        catch
        {
            return false;
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
            _client.DangerousRelease();
        }
    }
}
