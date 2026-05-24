namespace AlgoTradeForge.Storage;

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

/// <summary>
/// Defaults target Hetzner Object Storage (Falkenstein, <c>fsn1</c>). Other Hetzner regions
/// (<c>nbg1</c>, <c>hel1</c>) override both <see cref="Endpoint"/> and <see cref="Region"/>
/// to match. For real AWS, clear <see cref="Endpoint"/> to empty and set <see cref="Region"/>
/// to a real AWS region (e.g. <c>us-east-1</c>); the SDK then resolves via
/// <c>RegionEndpoint.GetBySystemName</c>. Other S3-compatible providers (Ceph RGW, MinIO,
/// R2, Wasabi, B2, DO Spaces) work by overriding <see cref="Endpoint"/> + <see cref="Region"/>.
/// </summary>
public sealed class S3StorageOptions
{
    public string Endpoint { get; set; } = "https://fsn1.your-objectstorage.com";
    public string Region { get; set; } = "fsn1";
    public string Bucket { get; set; } = "";
    public string KeyPrefix { get; set; } = "";
    public string AccessKeyId { get; set; } = "";
    public string SecretAccessKey { get; set; } = "";
}
