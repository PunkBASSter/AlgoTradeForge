using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Archive;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Infrastructure.Archive;
using AlgoTradeForge.HistoryLoader.Tests.TestData;
using AlgoTradeForge.HistoryLoader.WebApi.Endpoints;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Endpoints;

// Fake: on MaterializeMonth, writes a clean (deduped) 2-row partition for the month.
file sealed class CleanRewriteMaterializer(string feedName) : IArchiveMaterializer
{
    public string Exchange => "binance";
    public string FeedName => feedName;
    public bool Supports(string assetType) => true;

    public async Task<ArchiveMonthResult> MaterializeMonth(
        CollectionAsset asset, CollectionFeed feed, string assetDir, int year, int month, CancellationToken ct = default)
    {
        var dir = Path.Combine(assetDir, feedName);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{year:D4}-{month:D2}_{feed.Interval}.csv");
        // Ordering pin: the handler must delete the partition BEFORE calling MaterializeMonth.
        // If the file still exists here, the endpoint reordered delete-after-materialize (data loss risk).
        if (File.Exists(path))
            throw new InvalidOperationException("partition not deleted before materialize");
        await File.WriteAllTextAsync(path, "ts,oi,oi_usd\n1614556800000,1,2\n1614557100000,3,4\n", ct);
        return new ArchiveMonthResult(2, AvailableAtSource: true);
    }
}

public sealed class MaintenanceDedupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"atf-maint-{Guid.NewGuid():N}");
    public MaintenanceDedupTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Dedup_RewritesDoubledPartition_AndFixesRecordCount()
    {
        var ct = TestContext.Current.CancellationToken;
        var assetDir = Path.Combine(_root, "binance", "BTCUSDT_perp");
        var oiDir = Path.Combine(assetDir, FeedNames.OpenInterest);
        Directory.CreateDirectory(oiDir);
        // Doubled partition: 2 distinct slots, 4 lines.
        await File.WriteAllTextAsync(Path.Combine(oiDir, "2021-03_5m.csv"),
            "ts,oi,oi_usd\n1614556800000,1,2\n1614556800000,1,2\n1614557100000,3,4\n1614557100000,3,4\n", ct);

        var registry = new ArchiveMaterializerRegistry([new CleanRewriteMaterializer(FeedNames.OpenInterest)]);

        var asset = CollectionAssets.Perp("BTCUSDT", 2, CollectionAssets.Feed(FeedNames.OpenInterest, "5m"));
        var planSource = Substitute.For<ICollectionPlanSource>();
        planSource.Current.Returns(new CollectionPlan([asset], [], []));

        var index = Substitute.For<IHistoryIndex>();

        var statusStore = Substitute.For<IFeedStatusStore>();
        statusStore.Load(assetDir, FeedNames.OpenInterest, "5m", Arg.Any<CancellationToken>())
            .Returns(new FeedStatus { FeedName = FeedNames.OpenInterest, Interval = "5m", RecordCount = 4 });

        var options = Substitute.For<IOptionsMonitor<HistoryLoaderOptions>>();
        options.CurrentValue.Returns(new HistoryLoaderOptions { DataRoot = _root });

        await MaintenanceEndpoints.Dedup(
            new MaintenanceEndpoints.DedupRequest("binance", "BTCUSDT_perp"),
            planSource, registry, index, statusStore, options, NullLoggerFactory.Instance, ct);

        var (lines, distinct) = await PartitionAudit.Count(Path.Combine(oiDir, "2021-03_5m.csv"), ct);
        Assert.Equal(2, lines);
        Assert.Equal(2, distinct);

        await index.Received(1).DeleteMonthPartition(
            "binance", "BTCUSDT_perp", FeedNames.OpenInterest, "5m", "2021-03", Arg.Any<CancellationToken>());
        await statusStore.Received(1).Save(assetDir, FeedNames.OpenInterest, "5m",
            Arg.Is<FeedStatus>(s => s.RecordCount == 2), Arg.Any<CancellationToken>());
    }
}
