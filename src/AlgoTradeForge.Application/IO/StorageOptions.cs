namespace AlgoTradeForge.Application.IO;

public enum StorageBackend
{
    LocalFileSystem = 0,
    S3 = 1,
}

public sealed class StorageOptions
{
    public StorageBackend Backend { get; set; } = StorageBackend.LocalFileSystem;
    public LocalStorageOptions Local { get; set; } = new();
    public S3StorageOptions S3 { get; set; } = new();
}

public sealed class LocalStorageOptions
{
    /// <summary>
    /// Root for relative keys. Absolute keys always pass through regardless of this setting
    /// (legacy callers still using absolute paths keep working).
    /// </summary>
    public string DataRoot { get; set; } = "";
}

public sealed class S3StorageOptions
{
    public string Endpoint { get; set; } = "";
    public string Region { get; set; } = "us-east-1";
    public string Bucket { get; set; } = "";
    public string KeyPrefix { get; set; } = "";
    public string AccessKeyId { get; set; } = "";
    public string SecretAccessKey { get; set; } = "";
}
