namespace StorageGO;

/// <summary>Connection and NTLM authentication options for an SMB share.</summary>
public sealed class SmbClientOptions
{
    public required string Server { get; init; }

    public required string ShareName { get; init; }

    public required string Username { get; init; }

    public required string Password { get; init; }

    public string? Domain { get; init; }

    public int Port { get; init; } = 445;

    public TimeSpan DialTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Maximum file size buffered by Read. Streaming APIs do not apply this limit.
    /// </summary>
    public long MaxReadBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>Maximum bytes transferred per native call, from 1 byte to 4 MiB.</summary>
    public int ChunkSizeBytes { get; init; } = 1024 * 1024;
}
