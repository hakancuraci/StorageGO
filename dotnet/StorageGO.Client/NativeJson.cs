using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StorageGO;

internal static class NativeJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal static T Invoke<T>(Func<IntPtr> nativeCall)
    {
        var pointer = nativeCall();
        if (pointer == IntPtr.Zero)
        {
            throw new SmbException("Native SMB layer returned an empty response.");
        }

        try
        {
            var json = Marshal.PtrToStringUTF8(pointer)
                ?? throw new SmbException("Native SMB response is not valid UTF-8.");
            return JsonSerializer.Deserialize<T>(json, Options)
                ?? throw new SmbException("Native SMB response is empty JSON.");
        }
        catch (JsonException exception)
        {
            throw new SmbException($"Native SMB response is not valid JSON: {exception.Message}");
        }
        finally
        {
            NativeMethods.StorageGO_FreeResult(pointer);
        }
    }

    internal static void EnsureSuccess(bool success, string? error)
    {
        if (!success)
        {
            throw new SmbException(error ?? "SMB operation failed.");
        }
    }

    internal sealed record StatusResponse(bool Success, string? Error);

    internal sealed record OpenResponse(
        bool Success,
        ulong TransferId,
        string? FileName,
        long FileSize,
        string? MimeType,
        string? Error);

    internal sealed record ChunkResponse(
        bool Success,
        int BytesTransferred,
        bool Eof,
        string? Error);
}
