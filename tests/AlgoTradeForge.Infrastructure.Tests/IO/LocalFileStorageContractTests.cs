using AlgoTradeForge.Storage;
using Xunit;

namespace AlgoTradeForge.Infrastructure.Tests.IO;

public sealed class LocalFileStorageContractTests : FileStorageContractTests
{
    private readonly string _root;
    private readonly LocalStorageOptions _opts;

    public LocalFileStorageContractTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"LocalFsContract_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _opts = new LocalStorageOptions { DataRoot = _root };
        Storage = new LocalFileStorage(_opts);
    }

    protected override IFileStorage Storage { get; }
    protected override string Prefix => "scope";

    private string Resolve(string name) => Path.Combine(_root, name);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public override void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task WriteAllLines_ReplacingOpenFile_NeverLeavesDestinationAbsent()
    {
        var storage = new LocalFileStorage(_opts);
        await storage.WriteAllLines("f.csv", new[] { "a" }, Ct);
        // Real readers (ReadWithEtag, PartitionFileWriter consumers) open with Delete-sharing.
        // FileShare.Read WITHOUT Delete would block MoveFileEx even after the fix — that would be
        // a bad test, not a code bug. Open the reader exactly as production does.
        using var reader = new FileStream(Resolve("f.csv"), FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        await storage.WriteAllLines("f.csv", new[] { "b", "c" }, Ct);   // must not throw; dst always present
        Assert.Equal(new[] { "b", "c" }, await storage.ReadAllLines("f.csv", Ct));
    }
}
