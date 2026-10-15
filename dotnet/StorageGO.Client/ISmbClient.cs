namespace StorageGO;

/// <summary>Reads and writes files on the SMB share configured for this client.</summary>
public interface ISmbClient : IDisposable
{
    SmbFile Read(string remotePath);

    SmbWriteResult Write(
        string remotePath,
        ReadOnlyMemory<byte> content,
        SmbWriteOptions? options = null);
}
