using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Archive;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Archive;

public sealed class ArchiveBackfillServiceTests
{
    // Clock pinned to 2026-07-07T00:00:00Z as required by the brief.
    private static readonly TestClock Clock =
        new(new DateTimeOffset(2026, 7, 7, 0, 0, 0, TimeSpan.Zero));

    private static readonly long Jul1Ms =
        new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private static readonly AssetCollectionConfig Asset = new()
    {
        Symbol = "BTCUSDT",
        Type = "perpetual",
        Exchange = "binance",
        HistoryStart = new DateOnly(2026, 1, 1),
    };

    private static readonly FeedCollectionConfig Feed = new()
    {
        Name = "candles",
        Interval = "1h",
    };

    private readonly IArchiveMaterializer _materializer = Substitute.For<IArchiveMaterializer>();
    private readonly IMonthCoverageCalculator _coverage = Substitute.For<IMonthCoverageCalculator>();
    private readonly IFeedStatusStore _feedStatusStore = Substitute.For<IFeedStatusStore>();
    private readonly ISettingsWriter _settingsWriter = Substitute.For<ISettingsWriter>();

    public ArchiveBackfillServiceTests()
    {
        _feedStatusStore.Load(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<FeedStatus?>(null));

        _materializer.Exchange.Returns("binance");
        _materializer.FeedName.Returns("candles");
        _materializer.Supports(Arg.Any<string>()).Returns(true);

        // Default: no months covered, materializer returns available.
        _coverage.IsMonthCovered(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<IReadOnlyList<DataGap>>(),
                Arg.Any<CancellationToken>())
            .Returns(false);

        _materializer.MaterializeMonth(
                Arg.Any<AssetCollectionConfig>(), Arg.Any<FeedCollectionConfig>(),
                Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ArchiveMonthResult(100L, true)));
    }

    private ArchiveBackfillService BuildSut(IArchiveMaterializer? materializer = null)
    {
        var registry = new ArchiveMaterializerRegistry([materializer ?? _materializer]);
        return new ArchiveBackfillService(
            registry, _coverage, _feedStatusStore, _settingsWriter, Clock,
            NullLogger<ArchiveBackfillService>.Instance);
    }

    private static long Ms(int year, int month, int day = 1) =>
        new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    // -------------------------------------------------------------------------
    // 1. No materializer registered FOR THIS FEED → not replenishable →
    //    return fromMs unchanged and the (other-feed) materializer is never called.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task NotReplenishable_ReturnsFromMsUnchanged_AndMaterializesNothing()
    {
        // Registry contains a materializer for a DIFFERENT feed only, so the
        // no-call assertion below is meaningful (the stub is reachable in principle).
        var otherFeedMaterializer = Substitute.For<IArchiveMaterializer>();
        otherFeedMaterializer.Exchange.Returns("binance");
        otherFeedMaterializer.FeedName.Returns("funding-rate");
        otherFeedMaterializer.Supports(Arg.Any<string>()).Returns(true);

        var sut = BuildSut(otherFeedMaterializer);
        long from = Ms(2026, 3, 1);
        long to = Ms(2026, 7, 7);

        var result = await sut.CoverFromArchive(Asset, Feed, "/data", from, to, ct: TestContext.Current.CancellationToken);

        Assert.Equal(from, result);
        await otherFeedMaterializer.DidNotReceiveWithAnyArgs()
            .MaterializeMonth(default!, default!, default!, default, default, TestContext.Current.CancellationToken);
    }

    // -------------------------------------------------------------------------
    // 2. Covers missing closed months oldest-first; April (covered) skipped; July never touched.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CoversMissingClosedMonths_OldestFirst()
    {
        // April 2026 is already covered.
        _coverage.IsMonthCovered(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                2026, 4, Arg.Any<IReadOnlyList<DataGap>>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var callOrder = new List<(int y, int m)>();
        _materializer.MaterializeMonth(
                Arg.Any<AssetCollectionConfig>(), Arg.Any<FeedCollectionConfig>(),
                Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                callOrder.Add((ci.ArgAt<int>(3), ci.ArgAt<int>(4)));
                return Task.FromResult(new ArchiveMonthResult(100L, true));
            });

        var sut = BuildSut();
        await sut.CoverFromArchive(Asset, Feed, "/data", Ms(2026, 3), Ms(2026, 7, 7),
            ct: TestContext.Current.CancellationToken);

        // March, May, June in that order; April skipped; July never a candidate.
        Assert.Equal([(2026, 3), (2026, 5), (2026, 6)], callOrder);
    }

    // -------------------------------------------------------------------------
    // 3. Current month is never archive-touched; return value == Jul-1 start.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CurrentMonth_NeverArchiveTouched_ReturnStartOfCurrentMonth()
    {
        var sut = BuildSut();
        var result = await sut.CoverFromArchive(Asset, Feed, "/data", Ms(2026, 3), Ms(2026, 7, 7),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(Jul1Ms, result);

        // July must never have been passed to the materializer.
        await _materializer.DidNotReceive().MaterializeMonth(
            Arg.Any<AssetCollectionConfig>(), Arg.Any<FeedCollectionConfig>(),
            Arg.Any<string>(), 2026, 7, TestContext.Current.CancellationToken);
    }

    // -------------------------------------------------------------------------
    // 4. Range entirely in closed months → return toMs (= limit = toMs < currentMonthStart).
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RangeEntirelyInClosedMonths_ReturnsToMs()
    {
        long to = Ms(2026, 3, 1); // well before current-month start (Jul 1)

        var sut = BuildSut();
        var result = await sut.CoverFromArchive(Asset, Feed, "/data", Ms(2026, 1), to,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(to, result);
    }

    // -------------------------------------------------------------------------
    // 5. Range entirely in current month → no candidates, return fromMs unchanged.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RangeEntirelyInCurrentMonth_ReturnsFromMsUnchanged_NoMaterialization()
    {
        long from = Ms(2026, 7, 2);
        long to = Ms(2026, 7, 7);

        var sut = BuildSut();
        var result = await sut.CoverFromArchive(Asset, Feed, "/data", from, to,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(from, result);
        await _materializer.DidNotReceiveWithAnyArgs()
            .MaterializeMonth(default!, default!, default!, default, default, TestContext.Current.CancellationToken);
    }

    // -------------------------------------------------------------------------
    // 6. Leading unavailable months → discover and persist first available month.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task UnavailableLeadingMonths_PersistDiscoveredStart_ForConfiguredAsset()
    {
        // Jan and Feb return AvailableAtSource=false; everything else is true.
        _materializer.MaterializeMonth(
                Arg.Any<AssetCollectionConfig>(), Arg.Any<FeedCollectionConfig>(),
                Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                int year = ci.ArgAt<int>(3);
                int month = ci.ArgAt<int>(4);
                bool available = !(year == 2026 && month <= 2);
                return Task.FromResult(new ArchiveMonthResult(available ? 100L : 0L, available));
            });

        var sut = BuildSut();
        await sut.CoverFromArchive(Asset, Feed, "/data", Ms(2026, 1), Ms(2026, 7, 7),
            ct: TestContext.Current.CancellationToken);

        // Earliest available = March 2026.
        await _settingsWriter.Received(1).UpdateFeedHistoryStart(
            Asset.Symbol, Asset.Type, Feed.Name, Feed.Interval,
            new DateOnly(2026, 3, 1),
            TestContext.Current.CancellationToken);
    }

    // -------------------------------------------------------------------------
    // 7. Covered closed month → materializer not called (no forced re-download).
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CoveredClosedMonth_Skipped()
    {
        // Only May 2026 is covered.
        _coverage.IsMonthCovered(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                2026, 5, Arg.Any<IReadOnlyList<DataGap>>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var sut = BuildSut();
        await sut.CoverFromArchive(Asset, Feed, "/data", Ms(2026, 3), Ms(2026, 7, 7),
            ct: TestContext.Current.CancellationToken);

        // May must never be materialized.
        await _materializer.DidNotReceive().MaterializeMonth(
            Arg.Any<AssetCollectionConfig>(), Arg.Any<FeedCollectionConfig>(),
            Arg.Any<string>(), 2026, 5, TestContext.Current.CancellationToken);

        // March, April, June should be materialized.
        await _materializer.Received(1).MaterializeMonth(
            Arg.Any<AssetCollectionConfig>(), Arg.Any<FeedCollectionConfig>(),
            Arg.Any<string>(), 2026, 3, TestContext.Current.CancellationToken);
        await _materializer.Received(1).MaterializeMonth(
            Arg.Any<AssetCollectionConfig>(), Arg.Any<FeedCollectionConfig>(),
            Arg.Any<string>(), 2026, 4, TestContext.Current.CancellationToken);
        await _materializer.Received(1).MaterializeMonth(
            Arg.Any<AssetCollectionConfig>(), Arg.Any<FeedCollectionConfig>(),
            Arg.Any<string>(), 2026, 6, TestContext.Current.CancellationToken);
    }

    // -------------------------------------------------------------------------
    // 8. Recorded source gaps from FeedStatus flow into the coverage predicate.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RecordedGaps_FlowIntoCoverageCheck()
    {
        var gap = new DataGap { FromMs = Ms(2026, 3, 10), ToMs = Ms(2026, 3, 11) };
        var status = new FeedStatus
        {
            FeedName = Feed.Name,
            Interval = Feed.Interval,
            Gaps = [gap],
        };
        _feedStatusStore.Load("/data", Feed.Name, Feed.Interval, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<FeedStatus?>(status));

        var receivedGapLists = new List<IReadOnlyList<DataGap>>();
        _coverage.IsMonthCovered(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int>(), Arg.Any<int>(),
                Arg.Do<IReadOnlyList<DataGap>>(receivedGapLists.Add),
                Arg.Any<CancellationToken>())
            .Returns(false);

        var sut = BuildSut();
        await sut.CoverFromArchive(Asset, Feed, "/data", Ms(2026, 3), Ms(2026, 7, 7),
            ct: TestContext.Current.CancellationToken);

        // Every coverage call (Mar–Jun = 4) must receive the recorded gap list.
        Assert.Equal(4, receivedGapLists.Count);
        Assert.All(receivedGapLists, gaps => Assert.Equal([gap], gaps));
    }

    // -------------------------------------------------------------------------
    // 9. (C1) Mid-month from: the month containing fromMs is included in candidates.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task MidMonthFrom_FromMonthIncluded()
    {
        // fromMs = March 15 — March starts before March 15, so the old code excluded it.
        long from = Ms(2026, 3, 15);
        long to = Ms(2026, 5, 7);

        var called = new List<(int y, int m)>();
        _materializer.MaterializeMonth(
                Arg.Any<AssetCollectionConfig>(), Arg.Any<FeedCollectionConfig>(),
                Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                called.Add((ci.ArgAt<int>(3), ci.ArgAt<int>(4)));
                return Task.FromResult(new ArchiveMonthResult(100L, true));
            });

        var sut = BuildSut();
        await sut.CoverFromArchive(Asset, Feed, "/data", from, to,
            ct: TestContext.Current.CancellationToken);

        // March must be materialized even though it only partially overlaps the from-edge.
        Assert.Contains((2026, 3), called);
    }

    // -------------------------------------------------------------------------
    // 10. (C1) Mid-month to < currentMonth: the trailing month is included.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task MidMonthTo_TrailingMonthIncluded()
    {
        // toMs = May 15 (mid-month, before currentMonthStart = July 1).
        long from = Ms(2026, 3, 1);
        long to = Ms(2026, 5, 15);

        var called = new List<(int y, int m)>();
        _materializer.MaterializeMonth(
                Arg.Any<AssetCollectionConfig>(), Arg.Any<FeedCollectionConfig>(),
                Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                called.Add((ci.ArgAt<int>(3), ci.ArgAt<int>(4)));
                return Task.FromResult(new ArchiveMonthResult(100L, true));
            });

        var sut = BuildSut();
        await sut.CoverFromArchive(Asset, Feed, "/data", from, to,
            ct: TestContext.Current.CancellationToken);

        // May must be materialized even though toMs falls mid-month.
        Assert.Contains((2026, 5), called);
    }

    // -------------------------------------------------------------------------
    // 11. (I1a) Actual first-data DateOnly from reloaded FeedStatus is persisted.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task DiscoveredStart_UsesActualFirstTimestamp_WhenStatusAvailable()
    {
        // Jan and Feb unavailable; March is first available month.
        _materializer.MaterializeMonth(
                Arg.Any<AssetCollectionConfig>(), Arg.Any<FeedCollectionConfig>(),
                Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                int year = ci.ArgAt<int>(3);
                int month = ci.ArgAt<int>(4);
                bool available = !(year == 2026 && month <= 2);
                return Task.FromResult(new ArchiveMonthResult(available ? 100L : 0L, available));
            });

        // First call (for gaps): null; second call (I1a reload): status with FirstTimestamp = March 5.
        long march5Ms = Ms(2026, 3, 5);
        var reloadedStatus = new FeedStatus
        {
            FeedName = Feed.Name,
            Interval = Feed.Interval,
            FirstTimestamp = march5Ms,
        };
        int loadCallCount = 0;
        _feedStatusStore.Load(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                loadCallCount++;
                return Task.FromResult<FeedStatus?>(loadCallCount == 1 ? null : reloadedStatus);
            });

        var sut = BuildSut();
        await sut.CoverFromArchive(Asset, Feed, "/data", Ms(2026, 1), Ms(2026, 7, 7),
            ct: TestContext.Current.CancellationToken);

        // Must persist March 5, not March 1.
        await _settingsWriter.Received(1).UpdateFeedHistoryStart(
            Asset.Symbol, Asset.Type, Feed.Name, Feed.Interval,
            new DateOnly(2026, 3, 5),
            TestContext.Current.CancellationToken);
    }
}
