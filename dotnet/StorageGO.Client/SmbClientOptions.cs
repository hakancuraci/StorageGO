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
    /// Maximum file size returned by Read. The limit protects the process because
    /// the current native ABI returns the complete file in one response.
    /// </summary>
    public long MaxReadBytes { get; init; } = 256L * 1024 * 1024;
}
