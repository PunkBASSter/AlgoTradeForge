using AlgoTradeForge.Application.IO;
using AlgoTradeForge.Infrastructure.IO;

namespace AlgoTradeForge.Infrastructure.Tests.IO;

public sealed class LocalFileStorageContractTests : FileStorageContractTests
{
    private readonly string _root;

    public LocalFileStorageContractTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"LocalFsContract_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        Storage = new LocalFileStorage(new LocalStorageOptions { DataRoot = _root });
    }

    protected override IFileStorage Storage { get; }
    protected override string Prefix => "scope";

    public override void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
