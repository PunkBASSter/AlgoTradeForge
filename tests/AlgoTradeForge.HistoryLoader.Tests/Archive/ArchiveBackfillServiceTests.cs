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
    private readonly ISettingsWriter _settingsWriter = Substitute.For<ISettingsWriter>();

    public ArchiveBackfillServiceTests()
    {
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

    private ArchiveBackfillService BuildSut(bool withMaterializer = true)
    {
        IEnumerable<IArchiveMaterializer> materializers = withMaterializer ? [_materializer] : [];
        var registry = new ArchiveMaterializerRegistry(materializers);
        return new ArchiveBackfillService(
            registry, _coverage, _settingsWriter, Clock,
            NullLogger<ArchiveBackfillService>.Instance);
    }

    private static long Ms(int year, int month, int day = 1) =>
        new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    // -------------------------------------------------------------------------
    // 1. No materializer registered → feed not replenishable → return fromMs unchanged.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task NotReplenishable_ReturnsFromMsUnchanged_AndMaterializesNothing()
    {
        var sut = BuildSut(withMaterializer: false);
        long from = Ms(2026, 3, 1);
        long to = Ms(2026, 7, 7);

        var result = await sut.CoverFromArchive(Asset, Feed, "/data", from, to, ct: TestContext.Current.CancellationToken);

        Assert.Equal(from, result);
        await _materializer.DidNotReceiveWithAnyArgs()
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
}
