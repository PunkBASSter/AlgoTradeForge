using AlgoTradeForge.HistoryLoader.Infrastructure.Archive;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Archive;

public sealed class PartitionFileWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"atf-pfw-{Guid.NewGuid():N}");
    public PartitionFileWriterTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task ReplacePartition_CreatesFileWithHeaderAndRows()
    {
        var path = Path.Combine(_dir, "candles", "2024-03_1h.csv");
        var writer = new PartitionFileWriter();

        await writer.ReplacePartition(path, "ts,o,h,l,c,vol", ["1000,1,2,0,1,10", "2000,1,2,0,1,20"], TestContext.Current.CancellationToken);

        var lines = await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(["ts,o,h,l,c,vol", "1000,1,2,0,1,10", "2000,1,2,0,1,20"], lines);
    }

    [Fact]
    public async Task ReplacePartition_OverwritesExistingPartialFile()
    {
        var path = Path.Combine(_dir, "2024-03.csv");
        await File.WriteAllTextAsync(path, "ts,x\n999,0.5\n", TestContext.Current.CancellationToken);
        var writer = new PartitionFileWriter();

        await writer.ReplacePartition(path, "ts,x", ["1000,1.5"], TestContext.Current.CancellationToken);

        Assert.Equal(["ts,x", "1000,1.5"], await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp-*"));
    }
}
