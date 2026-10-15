using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StorageGO;

public sealed class SmbClient : ISmbClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly NativeClientHandle _handle;
    private bool _disposed;

    private SmbClient(NativeClientHandle handle) => _handle = handle;

    public static ISmbClient Create(SmbClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Validate(options);
        NativeMethods.EnsureInitialized();

        var config = new
        {
            options.Server,
            options.ShareName,
            options.Username,
            options.Password,
            options.Domain,
            options.Port,
            DialTimeoutSeconds = checked((int)Math.Ceiling(options.DialTimeout.TotalSeconds)),
            options.MaxReadBytes
        };

        var response = InvokeJson<CreateResponse>(() =>
            NativeMethods.StorageGO_CreateClient(JsonSerializer.Serialize(config, JsonOptions)));
        if (!response.Success || response.ClientId == 0)
        {
            throw new SmbException(response.Error ?? "Native SMB client oluşturulamadı.");
        }

        return new SmbClient(new NativeClientHandle(response.ClientId));
    }

    public SmbFile Read(string remotePath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);

        var response = Send(new NativeRequest("read", remotePath));
        return new SmbFile(
            response.FileName ?? Path.GetFileName(remotePath),
            response.FileSize,
            response.MimeType ?? "application/octet-stream",
            response.Content ?? []);
    }

    public SmbWriteResult Write(
        string remotePath,
        ReadOnlyMemory<byte> content,
        SmbWriteOptions? options = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        options ??= new SmbWriteOptions();

        var response = Send(new NativeRequest(
            "write",
            remotePath,
            content.ToArray(),
            options.Overwrite,
            options.ForceSync,
            options.MimeType));

        return new SmbWriteResult(
            response.FileName ?? Path.GetFileName(remotePath),
            response.FileSize,
            response.MimeType ?? options.MimeType ?? "application/octet-stream");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _handle.Dispose();
    }

    private NativeResponse Send(NativeRequest request)
    {
        var json = JsonSerializer.Serialize(request, JsonOptions);
        var response = InvokeJson<NativeResponse>(() => NativeMethods.StorageGO_Invoke(_handle, json));
        if (!response.Success)
        {
            throw new SmbException(response.Error ?? "SMB işlemi başarısız.");
        }

        return response;
    }

    private static T InvokeJson<T>(Func<IntPtr> nativeCall)
    {
        var pointer = nativeCall();
        if (pointer == IntPtr.Zero)
        {
            throw new SmbException("Native katman boş yanıt döndürdü.");
        }

        try
        {
            var json = Marshal.PtrToStringUTF8(pointer)
                ?? throw new SmbException("Native yanıt UTF-8 olarak okunamadı.");
            return JsonSerializer.Deserialize<T>(json, JsonOptions)
                ?? throw new SmbException("Native yanıt JSON olarak okunamadı.");
        }
        catch (JsonException exception)
        {
            throw new SmbException($"Native yanıt JSON olarak okunamadı: {exception.Message}");
        }
        finally
        {
            NativeMethods.StorageGO_FreeResult(pointer);
        }
    }

    private static void Validate(SmbClientOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Server);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ShareName);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Username);
        ArgumentNullException.ThrowIfNull(options.Password);
        if (options.Port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(options.Port));
        }
        if (options.DialTimeout <= TimeSpan.Zero || options.DialTimeout > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(options.DialTimeout));
        }
        if (options.MaxReadBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MaxReadBytes));
        }
    }

    private sealed record CreateResponse(bool Success, ulong ClientId, string? Error);

    private sealed record NativeRequest(
        string Action,
        string Path,
        byte[]? Content = null,
        bool Overwrite = false,
        bool ForceSync = false,
        string? MimeType = null);

    private sealed record NativeResponse(
        bool Success,
        string? Message,
        string? FileName,
        long FileSize,
        string? MimeType,
        byte[]? Content,
        string? Error);
}
