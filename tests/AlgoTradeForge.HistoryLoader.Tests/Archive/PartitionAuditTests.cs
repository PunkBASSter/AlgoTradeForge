using AlgoTradeForge.HistoryLoader.Infrastructure.Archive;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Archive;

public sealed class PartitionAuditTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"atf-audit-{Guid.NewGuid():N}");
    public PartitionAuditTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task Count_ReportsLinesAndDistinctTs()
    {
        var path = Path.Combine(_dir, "p.csv");
        await File.WriteAllTextAsync(path,
            "ts,oi,oi_usd\n1000,1,2\n1000,1,2\n2000,3,4\n2000,3,4\n",
            TestContext.Current.CancellationToken);

        var (lines, distinct) = await PartitionAudit.Count(path, TestContext.Current.CancellationToken);

        Assert.Equal(4, lines);
        Assert.Equal(2, distinct);
    }

    [Fact]
    public async Task Count_MissingFile_ReturnsZeros()
    {
        var (lines, distinct) = await PartitionAudit.Count(
            Path.Combine(_dir, "nope.csv"), TestContext.Current.CancellationToken);
        Assert.Equal(0, lines);
        Assert.Equal(0, distinct);
    }
}
