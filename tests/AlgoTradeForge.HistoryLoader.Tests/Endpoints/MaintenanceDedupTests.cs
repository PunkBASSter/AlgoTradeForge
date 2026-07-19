using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Archive;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Infrastructure.Archive;
using AlgoTradeForge.HistoryLoader.Tests.TestData;
using AlgoTradeForge.HistoryLoader.Tests.TestHelpers;
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
        // Inflated existing base: the recompute must overwrite it with the authoritative distinct total.
        var existingInput = new FeedStatus { FeedName = FeedNames.OpenInterest, Interval = "5m", RecordCount = 4 };

        var options = Substitute.For<IOptionsMonitor<HistoryLoaderOptions>>();
        options.CurrentValue.Returns(new HistoryLoaderOptions { DataRoot = _root });

        await MaintenanceEndpoints.Dedup(
            new MaintenanceEndpoints.DedupRequest("binance", "BTCUSDT_perp"),
            planSource, registry, index, statusStore, options, NullLoggerFactory.Instance,
            new TestClock(new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero)), ct);

        var (lines, distinct) = await PartitionAudit.Count(Path.Combine(oiDir, "2021-03_5m.csv"), ct);
        Assert.Equal(2, lines);
        Assert.Equal(2, distinct);

        await index.Received(1).DeleteMonthPartition(
            "binance", "BTCUSDT_perp", FeedNames.OpenInterest, "5m", "2021-03", Arg.Any<CancellationToken>());
        var captured = statusStore.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == "Update")
            .Select(c => ((Func<FeedStatus?, FeedStatus>)c.GetArguments()[3]!)(existingInput))
            .Single();
        Assert.Equal(2, captured.RecordCount);
    }

    [Fact]
    public async Task Dedup_MultiPartition_RepairsOnlyDoubled_AndRecomputesFeedTotal()
    {
        var ct = TestContext.Current.CancellationToken;
        var assetDir = Path.Combine(_root, "binance", "BTCUSDT_perp");
        var oiDir = Path.Combine(assetDir, FeedNames.OpenInterest);
        Directory.CreateDirectory(oiDir);
        // 2021-03 doubled: 4 lines, 2 distinct slots.
        await File.WriteAllTextAsync(Path.Combine(oiDir, "2021-03_5m.csv"),
            "ts,oi,oi_usd\n1614556800000,1,2\n1614556800000,1,2\n1614557100000,3,4\n1614557100000,3,4\n", ct);
        // 2021-04 already clean: 2 lines, 2 distinct slots (distinct April content to prove it stays untouched).
        var cleanApril = "ts,oi,oi_usd\n1617235200000,5,6\n1617235500000,7,8\n";
        var aprilPath = Path.Combine(oiDir, "2021-04_5m.csv");
        await File.WriteAllTextAsync(aprilPath, cleanApril, ct);

        var registry = new ArchiveMaterializerRegistry([new CleanRewriteMaterializer(FeedNames.OpenInterest)]);

        var asset = CollectionAssets.Perp("BTCUSDT", 2, CollectionAssets.Feed(FeedNames.OpenInterest, "5m"));
        var planSource = Substitute.For<ICollectionPlanSource>();
        planSource.Current.Returns(new CollectionPlan([asset], [], []));

        var index = Substitute.For<IHistoryIndex>();

        var statusStore = Substitute.For<IFeedStatusStore>();
        // Inflated existing base: the recompute must overwrite it with the authoritative distinct total.
        var existingInput = new FeedStatus { FeedName = FeedNames.OpenInterest, Interval = "5m", RecordCount = 6 };

        var options = Substitute.For<IOptionsMonitor<HistoryLoaderOptions>>();
        options.CurrentValue.Returns(new HistoryLoaderOptions { DataRoot = _root });

        await MaintenanceEndpoints.Dedup(
            new MaintenanceEndpoints.DedupRequest("binance", "BTCUSDT_perp"),
            planSource, registry, index, statusStore, options, NullLoggerFactory.Instance,
            new TestClock(new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero)), ct);

        // Only the doubled month is re-materialized.
        await index.Received(1).DeleteMonthPartition(
            "binance", "BTCUSDT_perp", FeedNames.OpenInterest, "5m", "2021-03", Arg.Any<CancellationToken>());
        await index.DidNotReceive().DeleteMonthPartition(
            "binance", "BTCUSDT_perp", FeedNames.OpenInterest, "5m", "2021-04", Arg.Any<CancellationToken>());

        // The clean partition is byte-for-byte untouched.
        Assert.Equal(cleanApril, await File.ReadAllTextAsync(aprilPath, ct));

        // Authoritative recompute across the whole feed dir: 2 (now-clean 2021-03) + 2 (2021-04) = 4.
        var captured = statusStore.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == "Update")
            .Select(c => ((Func<FeedStatus?, FeedStatus>)c.GetArguments()[3]!)(existingInput))
            .Single();
        Assert.Equal(4, captured.RecordCount);
    }

    [Fact]
    public async Task Dedup_SecondRun_IsNoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        var assetDir = Path.Combine(_root, "binance", "BTCUSDT_perp");
        var oiDir = Path.Combine(assetDir, FeedNames.OpenInterest);
        Directory.CreateDirectory(oiDir);
        await File.WriteAllTextAsync(Path.Combine(oiDir, "2021-03_5m.csv"),
            "ts,oi,oi_usd\n1614556800000,1,2\n1614556800000,1,2\n1614557100000,3,4\n1614557100000,3,4\n", ct);

        var registry = new ArchiveMaterializerRegistry([new CleanRewriteMaterializer(FeedNames.OpenInterest)]);

        var asset = CollectionAssets.Perp("BTCUSDT", 2, CollectionAssets.Feed(FeedNames.OpenInterest, "5m"));
        var planSource = Substitute.For<ICollectionPlanSource>();
        planSource.Current.Returns(new CollectionPlan([asset], [], []));

        var index = Substitute.For<IHistoryIndex>();

        var statusStore = Substitute.For<IFeedStatusStore>();

        var options = Substitute.For<IOptionsMonitor<HistoryLoaderOptions>>();
        options.CurrentValue.Returns(new HistoryLoaderOptions { DataRoot = _root });

        var request = new MaintenanceEndpoints.DedupRequest("binance", "BTCUSDT_perp");

        // First run cleans the doubled partition.
        await MaintenanceEndpoints.Dedup(
            request, planSource, registry, index, statusStore, options, NullLoggerFactory.Instance,
            new TestClock(new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero)), ct);
        await index.Received(1).DeleteMonthPartition(
            "binance", "BTCUSDT_perp", FeedNames.OpenInterest, "5m", "2021-03", Arg.Any<CancellationToken>());

        index.ClearReceivedCalls();
        statusStore.ClearReceivedCalls();

        // Second run: all partitions clean → no delete, no re-materialize (CleanRewriteMaterializer would
        // throw on an existing file), no recompute (months.Count == 0 short-circuits before Update).
        await MaintenanceEndpoints.Dedup(
            request, planSource, registry, index, statusStore, options, NullLoggerFactory.Instance,
            new TestClock(new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero)), ct);

        await index.DidNotReceive().DeleteMonthPartition(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        await statusStore.DidNotReceive().Update(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<Func<FeedStatus?, FeedStatus>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Dedup_SkipsCurrentMonth_RepairsPast()
    {
        var ct = TestContext.Current.CancellationToken;
        // Clock pinned so the current month is 2026-07.
        var clock = new TestClock(new DateTimeOffset(2026, 7, 19, 0, 0, 0, TimeSpan.Zero));
        var assetDir = Path.Combine(_root, "binance", "BTCUSDT_perp");
        var oiDir = Path.Combine(assetDir, FeedNames.OpenInterest);
        Directory.CreateDirectory(oiDir);
        var doubled = "ts,oi,oi_usd\n1614556800000,1,2\n1614556800000,1,2\n1614557100000,3,4\n1614557100000,3,4\n";
        await File.WriteAllTextAsync(Path.Combine(oiDir, "2021-03_5m.csv"), doubled, ct); // past, doubled
        var curPath = Path.Combine(oiDir, "2026-07_5m.csv");
        await File.WriteAllTextAsync(curPath, doubled, ct);                               // current, doubled

        var registry = new ArchiveMaterializerRegistry([new CleanRewriteMaterializer(FeedNames.OpenInterest)]);
        var asset = CollectionAssets.Perp("BTCUSDT", 2, CollectionAssets.Feed(FeedNames.OpenInterest, "5m"));
        var planSource = Substitute.For<ICollectionPlanSource>();
        planSource.Current.Returns(new CollectionPlan([asset], [], []));
        var index = Substitute.For<IHistoryIndex>();
        var statusStore = Substitute.For<IFeedStatusStore>();
        var options = Substitute.For<IOptionsMonitor<HistoryLoaderOptions>>();
        options.CurrentValue.Returns(new HistoryLoaderOptions { DataRoot = _root });

        await MaintenanceEndpoints.Dedup(
            new MaintenanceEndpoints.DedupRequest("binance", "BTCUSDT_perp"),
            planSource, registry, index, statusStore, options, NullLoggerFactory.Instance, clock, ct);

        // Past month repaired.
        await index.Received(1).DeleteMonthPartition(
            "binance", "BTCUSDT_perp", FeedNames.OpenInterest, "5m", "2021-03", Arg.Any<CancellationToken>());
        // Current month never touched — the archive lags live by ~a day, so re-materializing would roll it
        // back; the live collector owns it and any trivial live-path dup self-heals there.
        await index.DidNotReceive().DeleteMonthPartition(
            "binance", "BTCUSDT_perp", FeedNames.OpenInterest, "5m", "2026-07", Arg.Any<CancellationToken>());
        var (lines, distinct) = await PartitionAudit.Count(curPath, ct);
        Assert.True(lines > distinct, "current-month partition must be left untouched");
    }
}
