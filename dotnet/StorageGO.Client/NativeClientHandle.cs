using Microsoft.Win32.SafeHandles;

namespace StorageGO;

internal sealed class NativeClientHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private NativeClientHandle() : base(ownsHandle: true) { }

    internal NativeClientHandle(ulong clientId) : this()
    {
        SetHandle(unchecked((IntPtr)(long)clientId));
    }

    protected override bool ReleaseHandle()
    {
        // SafeHandle release can run on the finalizer thread and must never throw.
        try
        {
            var response = NativeMethods.StorageGO_CloseClient(unchecked((ulong)handle.ToInt64()));
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
    }
}
