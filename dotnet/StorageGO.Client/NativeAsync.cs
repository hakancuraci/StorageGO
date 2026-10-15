using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace StorageGO;

/// <summary>Owns managed operation state until the native completion callback has finished copying data.</summary>
internal static class NativeAsync
{
    private static readonly ConcurrentDictionary<ulong, IOperation> Pending = new();
    // The delegate must remain rooted for the entire process, including callbacks after client disposal.
    private static readonly NativeMethods.Completion Completion = Complete;
    private static long _nextToken;

    internal sealed record Request(
        string Action,
        ulong ClientId = 0,
        ulong TransferId = 0,
        string? Path = null,
        bool Overwrite = false,
        bool ForceSync = false);

    internal static Task<T> Invoke<T>(
        Request request,
        SafeHandle? handle = null,
        byte[]? writeBuffer = null,
        int count = 0,
        byte[]? readBuffer = null,
        CancellationToken cancellationToken = default) where T : NativeJson.IResponse
    {
        cancellationToken.ThrowIfCancellationRequested();
        var token = unchecked((ulong)Interlocked.Increment(ref _nextToken));
        var operation = new Operation<T>(handle, readBuffer, count, cancellationToken);
        if (!Pending.TryAdd(token, operation))
        {
            operation.Reject(new SmbException("Unable to allocate a native operation token."));
            return operation.Task;
        }

        operation.RegisterCancellation();
        try
        {
            var id = NativeMethods.StorageGO_BeginAsync(
                JsonSerializer.Serialize(request, NativeJson.Options), writeBuffer,
                checked((uint)count), Completion, token);
            if (id == 0)
            {
                if (Pending.TryRemove(token, out var rejected))
                {
                    rejected.Reject(new SmbException("Native async operation admission limit was reached. Retry after pending operations complete."));
                }
            }
            else
            {
                operation.PublishOperationId(id);
            }
        }
        catch (Exception exception)
        {
            if (Pending.TryRemove(token, out var rejected))
            {
                rejected.Reject(exception);
            }
        }

        return operation.Task;
    }

    private static void Complete(ulong token, IntPtr json, IntPtr readData, uint dataLength)
    {
        // No exception is allowed to cross a reverse P/Invoke frame into Go/C.
        try
        {
            if (Pending.TryRemove(token, out var operation))
            {
                operation.Complete(json, readData, dataLength);
            }
        }
        catch
        {
            // Operation.Complete captures all ordinary parse/copy/cleanup exceptions itself.
        }
    }

    private interface IOperation
    {
        void Complete(IntPtr json, IntPtr readData, uint dataLength);
        void Reject(Exception exception);
    }

    private sealed class Operation<T> : IOperation where T : NativeJson.IResponse
    {
        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly SafeHandle? _handle;
        private readonly byte[]? _readBuffer;
        private readonly int _capacity;
        private readonly CancellationToken _cancellationToken;
        private CancellationTokenRegistration _registration;
        private bool _handleReferenceHeld;
        private long _operationId;
        private int _cancelRequested;
        private int _completed;

        internal Operation(SafeHandle? handle, byte[]? readBuffer, int capacity, CancellationToken cancellationToken)
        {
            _handle = handle;
            _readBuffer = readBuffer;
            _capacity = capacity;
            _cancellationToken = cancellationToken;
            _handle?.DangerousAddRef(ref _handleReferenceHeld);
        }

        internal Task<T> Task => _completion.Task;

        internal void RegisterCancellation()
        {
            _registration = _cancellationToken.UnsafeRegister(static state => ((Operation<T>)state!).Cancel(), this);
            if (Volatile.Read(ref _completed) != 0)
            {
                _registration.Unregister();
            }
        }

        internal void PublishOperationId(ulong operationId)
        {
            Volatile.Write(ref _operationId, unchecked((long)operationId));
            if (Volatile.Read(ref _cancelRequested) != 0)
            {
                Cancel();
            }
        }

        private void Cancel()
        {
            Volatile.Write(ref _cancelRequested, 1);
            var id = unchecked((ulong)Volatile.Read(ref _operationId));
            if (id != 0)
            {
                try
                {
                    NativeMethods.StorageGO_CancelAsync(id);
                }
                catch
                {
                    // Native completion remains responsible for ending the operation and its lifetime.
                }
            }
        }

        public void Complete(IntPtr json, IntPtr readData, uint dataLength)
        {
            T? result = default;
            Exception? failure = null;
            try
            {
                var text = Marshal.PtrToStringUTF8(json)
                    ?? throw new SmbException("Native async operation returned an empty response.");
                result = JsonSerializer.Deserialize<T>(text, NativeJson.Options)
                    ?? throw new SmbException("Native async operation returned empty JSON.");
                if (dataLength > 0)
                {
                    if (_readBuffer is null || dataLength > _capacity || dataLength > _readBuffer.Length || readData == IntPtr.Zero)
                    {
                        throw new SmbException("Native async read returned an invalid buffer length.");
                    }

                    Marshal.Copy(readData, _readBuffer, 0, checked((int)dataLength));
                }

                if (_readBuffer is not null && result.Success && !result.Cancelled &&
                    result is NativeJson.ChunkResponse chunk && chunk.BytesTransferred != dataLength)
                {
                    throw new SmbException("Native async read byte count does not match its data length.");
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            Finish(result, failure);
        }

        public void Reject(Exception exception) => Finish(default, exception);

        private void Finish(T? result, Exception? exception)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
            {
                return;
            }

            try
            {
                // Unregister never waits for a concurrently executing cancellation callback.
                _registration.Unregister();
                if (_handleReferenceHeld)
                {
                    _handleReferenceHeld = false;
                    _handle!.DangerousRelease();
                }
            }
            catch (Exception cleanupError)
            {
                exception ??= cleanupError;
            }

            if (exception is not null)
            {
                _completion.TrySetException(exception);
            }
            else if (result!.Cancelled)
            {
                _completion.TrySetCanceled(_cancellationToken.IsCancellationRequested
                    ? _cancellationToken : new CancellationToken(canceled: true));
            }
            else
            {
                _completion.TrySetResult(result);
            }
        }
    }
}
