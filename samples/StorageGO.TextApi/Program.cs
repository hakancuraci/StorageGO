using System.Text;
using Microsoft.AspNetCore.Diagnostics;
using StorageGO;

var builder = WebApplication.CreateBuilder(args);

var maxTextBytes = builder.Configuration.GetValue<long?>("TextApi:MaxTextBytes")
    ?? 16L * 1024 * 1024;
if (maxTextBytes <= 0 || maxTextBytes > Array.MaxLength)
{
    throw new InvalidOperationException("TextApi:MaxTextBytes must be between 1 and Array.MaxLength.");
}

builder.WebHost.ConfigureKestrel(options =>
    options.Limits.MaxRequestBodySize = checked(maxTextBytes + 1024 * 1024));
builder.Services.AddProblemDetails();
builder.Services.AddSingleton(new TextApiLimits(maxTextBytes));
builder.Services.AddSingleton<ISmbClient>(services =>
{
    var configuration = services.GetRequiredService<IConfiguration>();
    var port = configuration.GetValue<int?>("Smb:Port") ?? 445;
    if (port is < 1 or > 65535)
    {
        throw new InvalidOperationException("Smb:Port must be between 1 and 65535.");
    }

    return SmbClient.Create(new SmbClientOptions
    {
        Server = Required(configuration, "Smb:Server"),
        Port = port,
        ShareName = Required(configuration, "Smb:ShareName"),
        Username = Required(configuration, "Smb:Username"),
        Password = Required(configuration, "Smb:Password"),
        Domain = configuration["Smb:Domain"],
        DialTimeout = TimeSpan.FromSeconds(15),
        MaxReadBytes = maxTextBytes,
        ChunkSizeBytes = 1024 * 1024,
        ClearBuffersOnReturn = true
    });
});

var app = builder.Build();

app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var exception = context.Features.Get<IExceptionHandlerPathFeature>()?.Error;
    var (status, title, detail) = exception switch
    {
        SmbException smbException =>
            (StatusCodes.Status502BadGateway, "SMB operation failed", smbException.Message),
        DecoderFallbackException =>
            (StatusCodes.Status422UnprocessableEntity, "The SMB file is not valid UTF-8", (string?)null),
        OperationCanceledException =>
            (StatusCodes.Status408RequestTimeout, "The operation was cancelled", (string?)null),
        _ =>
            (StatusCodes.Status500InternalServerError, "An unexpected error occurred", (string?)null)
    };

    await Results.Problem(statusCode: status, title: title, detail: detail).ExecuteAsync(context);
}));

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapGet("/read", async (string path, ISmbClient smbClient, CancellationToken cancellationToken) =>
{
    if (!RemotePath.TryNormalize(path, out var remotePath, out var error))
    {
        return Results.BadRequest(new { error });
    }

    var file = await smbClient.ReadAsync(remotePath, cancellationToken);
    var content = TextEncoding.Utf8Strict.GetString(file.Content);

    return Results.Ok(new ReadTextResponse(
        remotePath,
        content,
        file.FileSize,
        file.MimeType));
});

app.MapPost("/write", async (
    WriteTextRequest request,
    ISmbClient smbClient,
    TextApiLimits limits,
    CancellationToken cancellationToken) =>
{
    if (!RemotePath.TryNormalize(request.Path, out var remotePath, out var error))
    {
        return Results.BadRequest(new { error });
    }

    if (request.Content is null)
    {
        return Results.BadRequest(new { error = "Content is required." });
    }

    var content = TextEncoding.Utf8NoBom.GetBytes(request.Content);
    if (content.LongLength > limits.MaxTextBytes)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status413PayloadTooLarge,
            title: $"UTF-8 content cannot exceed {limits.MaxTextBytes} bytes.");
    }

    var result = await smbClient.WriteAsync(
        remotePath,
        content,
        new SmbWriteOptions
        {
            Overwrite = request.Overwrite,
            ForceSync = true,
            MimeType = "text/plain; charset=utf-8"
        },
        cancellationToken);

    return Results.Ok(new WriteTextResponse(
        remotePath,
        result.FileSize,
        result.MimeType));
});

// Resolve once at startup so missing configuration/native runtime errors fail fast.
_ = app.Services.GetRequiredService<ISmbClient>();

app.Run();

static string Required(IConfiguration configuration, string key)
{
    var value = configuration[key];
    return !string.IsNullOrWhiteSpace(value)
        ? value
        : throw new InvalidOperationException($"Configuration value '{key}' is required.");
}

internal sealed record TextApiLimits(long MaxTextBytes);

internal sealed record WriteTextRequest(string? Path, string? Content, bool Overwrite = true);

internal sealed record ReadTextResponse(string Path, string Content, long ByteCount, string MimeType);

internal sealed record WriteTextResponse(string Path, long ByteCount, string MimeType);

internal static class TextEncoding
{
    internal static readonly UTF8Encoding Utf8NoBom = new(false);
    internal static readonly UTF8Encoding Utf8Strict = new(false, true);
}

internal static class RemotePath
{
    internal static bool TryNormalize(string? path, out string normalized, out string error)
    {
        normalized = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "Path is required.";
            return false;
        }

        var candidate = path.Trim().Replace('\\', '/');
        if (candidate.StartsWith('/') || candidate.Contains('\0'))
        {
            error = "Path must be relative to the configured SMB share.";
            return false;
        }

        var segments = candidate.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".." || segment.Contains(':')))
        {
            error = "Path contains an invalid segment.";
            return false;
        }

        normalized = string.Join('/', segments);
        return true;
    }
}
