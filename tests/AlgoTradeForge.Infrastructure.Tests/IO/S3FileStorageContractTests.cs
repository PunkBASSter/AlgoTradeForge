using AlgoTradeForge.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AlgoTradeForge.Infrastructure.Tests.IO;

/// <summary>
/// Runs the <see cref="FileStorageContractTests"/> suite against an S3-compatible endpoint
/// (Hetzner Object Storage by default; also MinIO, Ceph RGW, real AWS, R2, …). Gated on the
/// <c>STORAGE_TEST_S3</c> env var so the default CI run does not require network or credentials.
/// Configure via:
/// <list type="bullet">
///   <item><c>STORAGE_TEST_S3</c> — any non-empty value enables the suite.</item>
///   <item><c>STORAGE_TEST_S3_BUCKET</c> — required bucket (must exist).</item>
///   <item><c>STORAGE_TEST_S3_ENDPOINT</c> — defaults to <c>https://fsn1.your-objectstorage.com</c>
///         (Hetzner Falkenstein). For MinIO use e.g. <c>http://localhost:9000</c>.</item>
///   <item><c>STORAGE_TEST_S3_REGION</c> — defaults to <c>fsn1</c>. Must match the endpoint
///         subdomain for Hetzner; any value for Ceph; a real AWS region when endpoint is empty.</item>
///   <item><c>STORAGE_TEST_S3_ACCESS_KEY</c>, <c>STORAGE_TEST_S3_SECRET_KEY</c> — optional;
///         falls back to the default AWS credential chain.</item>
/// </list>
/// Each test instance scopes writes under a unique <see cref="S3StorageOptions.KeyPrefix"/>
/// and clears that prefix on dispose.
/// </summary>
public sealed class S3FileStorageContractTests : FileStorageContractTests
{
    private readonly S3FileStorage _storage = null!;

    public S3FileStorageContractTests()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("STORAGE_TEST_S3")))
            Assert.Skip("STORAGE_TEST_S3 is not set — S3 contract suite disabled.");

        var bucket = Environment.GetEnvironmentVariable("STORAGE_TEST_S3_BUCKET")
            ?? throw new InvalidOperationException("STORAGE_TEST_S3_BUCKET must be set when STORAGE_TEST_S3 is enabled");

        var options = new S3StorageOptions
        {
            Bucket = bucket,
            Endpoint = Environment.GetEnvironmentVariable("STORAGE_TEST_S3_ENDPOINT") ?? "https://fsn1.your-objectstorage.com",
            Region = Environment.GetEnvironmentVariable("STORAGE_TEST_S3_REGION") ?? "fsn1",
            AccessKeyId = Environment.GetEnvironmentVariable("STORAGE_TEST_S3_ACCESS_KEY") ?? "",
            SecretAccessKey = Environment.GetEnvironmentVariable("STORAGE_TEST_S3_SECRET_KEY") ?? "",
            KeyPrefix = $"contract-test/{Guid.NewGuid():N}",
        };

        _storage = new S3FileStorage(options, NullLogger<S3FileStorage>.Instance);
    }

    protected override IFileStorage Storage => _storage;
    protected override string Prefix => "scope";

    public override void Dispose()
    {
        if (_storage is null) return;
        try { _storage.DeleteByPrefix("", TestContext.Current.CancellationToken).GetAwaiter().GetResult(); }
        catch { /* best effort — cleaning a test prefix should never fail the run */ }
        _storage.Dispose();
    }
}
