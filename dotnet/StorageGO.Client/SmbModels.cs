namespace StorageGO;

public sealed record SmbFile(
    string FileName,
    long FileSize,
    string MimeType,
    byte[] Content);

public sealed record SmbWriteResult(
    string FileName,
    long FileSize,
    string MimeType);

public sealed class SmbWriteOptions
{
    public bool Overwrite { get; init; }

    /// <summary>Syncs the remote file before closing a successful upload.</summary>
    public bool ForceSync { get; init; }

    public string? MimeType { get; init; }
}

public sealed class SmbException : Exception
{
    public SmbException(string message) : base(message) { }

    /// <summary>True only when the native layer positively identifies a normal, reusable file error.</summary>
    public bool ConnectionReusable { get; init; }
}
