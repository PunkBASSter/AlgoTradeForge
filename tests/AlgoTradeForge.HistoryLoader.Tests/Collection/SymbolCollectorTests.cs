using System.Net;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Archive;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Tests.TestData;
using AlgoTradeForge.HistoryLoader.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Collection;

public sealed class SymbolCollectorTests
{
    private readonly IFeedCollector _collector = Substitute.For<IFeedCollector>();
    private readonly IHistoryIndex _index = Substitute.For<IHistoryIndex>();
    private readonly CollectionChangeNotifier _notifier = new();
    private readonly SymbolCollector _sut;

    private static readonly CollectionAsset Asset = CollectionAssets.Perp("BTCUSDT");

    private static readonly CollectionFeed Feed = CollectionAssets.Feed("open-interest", "5m");

    // 2020-01-01 00:00:00 UTC
    private static readonly long FromMs = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero)
        .ToUnixTimeMilliseconds();

    private static readonly long ToMs = new DateTimeOffset(2020, 12, 1, 0, 0, 0, TimeSpan.Zero)
        .ToUnixTimeMilliseconds();

    // The threshold: data is available starting 2020-08-01
    private static readonly long ValidStartMs = new DateTimeOffset(2020, 8, 1, 0, 0, 0, TimeSpan.Zero)
        .ToUnixTimeMilliseconds();

    public SymbolCollectorTests()
    {
        _collector.FeedName.Returns("open-interest");
        _collector.SupportsSpot.Returns(true);

        // Empty registry → CoverFromArchive is always a no-op (returns fromMs unchanged).
        var archiveBackfill = new ArchiveBackfillService(
            new ArchiveMaterializerRegistry([]),
            Substitute.For<IMonthCoverageCalculator>(),
            Substitute.For<IFeedStatusStore>(),
            Substitute.For<IHistoryIndex>(),
            new CollectionChangeNotifier(),
            new TestClock(new DateTimeOffset(2026, 7, 7, 0, 0, 0, TimeSpan.Zero)),
            NullLogger<ArchiveBackfillService>.Instance);

        _sut = new SymbolCollector(
            [_collector],
            archiveBackfill,
            _index,
            _notifier,
            NullLogger<SymbolCollector>.Instance);
    }

    /// <summary>
    /// Sets up the mock to succeed when fromMs >= validStart, fail with date-range
    /// error otherwise. This simulates a Binance endpoint that only has data from
    /// a certain date onward.
    /// </summary>
    private void SetupDateThreshold(long validStart)
    {
        _collector.Collect(
                Arg.Any<CollectionAsset>(), Arg.Any<CollectionFeed>(),
                Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var from = ci.ArgAt<long>(3);
                if (from < validStart)
                    throw new DataSourceApiException(
                        -1130, "parameter 'startTime' is invalid.",
                        HttpStatusCode.BadRequest, isDateRangeError: true);
                return Task.CompletedTask;
            });
    }

    // -------------------------------------------------------------------------
    // 1. Date-range 400 → binary search finds valid start, persists to index + fires notifier
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CollectFeed_DateRange400_BinarySearchFindsAndPersists()
    {
        SetupDateThreshold(ValidStartMs);

        bool notified = false;
        _notifier.DiscoveryRecorded += () => notified = true;

        await _sut.CollectFeed(Asset, Feed, "/data", FromMs, ToMs, ct: CancellationToken.None);

        // Should persist August 2020 (month precision) as the discovered start.
        await _index.Received(1).SetDiscoveredFirstMonth(
            "binance", "BTCUSDT_perp", "open-interest", "5m", "2020-08",
            Arg.Any<CancellationToken>());

        Assert.True(notified);
    }

    // -------------------------------------------------------------------------
    // 1b. Binary search uses O(log n) probes, not O(n)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CollectFeed_BinarySearch_UsesLogarithmicProbes()
    {
        int callCount = 0;
        _collector.Collect(
                Arg.Any<CollectionAsset>(), Arg.Any<CollectionFeed>(),
                Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                callCount++;
                var from = ci.ArgAt<long>(3);
                if (from < ValidStartMs)
                    throw new DataSourceApiException(
                        -1130, "parameter 'startTime' is invalid.",
                        HttpStatusCode.BadRequest, isDateRangeError: true);
                return Task.CompletedTask;
            });

        await _sut.CollectFeed(Asset, Feed, "/data", FromMs, ToMs, ct: CancellationToken.None);

        // Jan–Dec 2020 = 12 months. Binary search ≤ log2(12) + 1 ≈ 5 probes.
        // Plus 1 initial attempt + 1 final full collection = ~7 total.
        // Linear would be 8+ (7 advances + 1 success + 1 full).
        Assert.True(callCount <= 8,
            $"Expected ≤8 API calls for binary search over 12 months, got {callCount}");
    }

    // -------------------------------------------------------------------------
    // 2. Non-date-range API error → skips without binary search
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CollectFeed_InvalidSymbol_DoesNotRetry()
    {
        _collector.Collect(
                Arg.Any<CollectionAsset>(), Arg.Any<CollectionFeed>(),
                Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Throws(new DataSourceApiException(-1121, "Invalid symbol.", HttpStatusCode.BadRequest));

        await _sut.CollectFeed(Asset, Feed, "/data", FromMs, ToMs, ct: CancellationToken.None);

        await _collector.Received(1).Collect(
            Arg.Any<CollectionAsset>(), Arg.Any<CollectionFeed>(),
            Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
        await _index.DidNotReceiveWithAnyArgs()
            .SetDiscoveredFirstMonth(default!, default!, default!, default!, default!, TestContext.Current.CancellationToken);
    }

    // -------------------------------------------------------------------------
    // 2b. Endpoint maintenance → does not retry
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CollectFeed_EndpointMaintenance_DoesNotRetry()
    {
        _collector.Collect(
                Arg.Any<CollectionAsset>(), Arg.Any<CollectionFeed>(),
                Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Throws(new DataSourceApiException(
                -1, "The endpoint has been out of maintenance", HttpStatusCode.BadRequest));

        await _sut.CollectFeed(Asset, Feed, "/data", FromMs, ToMs, ct: CancellationToken.None);

        await _collector.Received(1).Collect(
            Arg.Any<CollectionAsset>(), Arg.Any<CollectionFeed>(),
            Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
        await _index.DidNotReceiveWithAnyArgs()
            .SetDiscoveredFirstMonth(default!, default!, default!, default!, default!, TestContext.Current.CancellationToken);
    }

    // -------------------------------------------------------------------------
    // 3. Plain HttpRequestException 400 → does not retry
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CollectFeed_PlainHttp400_DoesNotRetry()
    {
        _collector.Collect(
                Arg.Any<CollectionAsset>(), Arg.Any<CollectionFeed>(),
                Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("Bad Request", null, HttpStatusCode.BadRequest));

        await _sut.CollectFeed(Asset, Feed, "/data", FromMs, ToMs, ct: CancellationToken.None);

        await _collector.Received(1).Collect(
            Arg.Any<CollectionAsset>(), Arg.Any<CollectionFeed>(),
            Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
        await _index.DidNotReceiveWithAnyArgs()
            .SetDiscoveredFirstMonth(default!, default!, default!, default!, default!, TestContext.Current.CancellationToken);
    }

    // -------------------------------------------------------------------------
    // 4. All months fail → gives up, does not persist
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CollectFeed_AllMonthsFail_GivesUp()
    {
        _collector.Collect(
                Arg.Any<CollectionAsset>(), Arg.Any<CollectionFeed>(),
                Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Throws(new DataSourceApiException(
                -1, "Invalid period.", HttpStatusCode.BadRequest, isDateRangeError: true));

        await _sut.CollectFeed(Asset, Feed, "/data", FromMs, ToMs, ct: CancellationToken.None);

        await _index.DidNotReceiveWithAnyArgs()
            .SetDiscoveredFirstMonth(default!, default!, default!, default!, default!, TestContext.Current.CancellationToken);
    }

    // -------------------------------------------------------------------------
    // 5. Success on first try → no probing, does not persist
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CollectFeed_SuccessOnFirstTry_DoesNotPersist()
    {
        _collector.Collect(
                Arg.Any<CollectionAsset>(), Arg.Any<CollectionFeed>(),
                Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _sut.CollectFeed(Asset, Feed, "/data", FromMs, ToMs, ct: CancellationToken.None);

        await _collector.Received(1).Collect(
            Arg.Any<CollectionAsset>(), Arg.Any<CollectionFeed>(),
            Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
        await _index.DidNotReceiveWithAnyArgs()
            .SetDiscoveredFirstMonth(default!, default!, default!, default!, default!, TestContext.Current.CancellationToken);
    }

    // -------------------------------------------------------------------------
    // 6. Final full collection uses discovered start, not toMs
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CollectFeed_FinalCollection_UsesDiscoveredStart()
    {
        SetupDateThreshold(ValidStartMs);

        await _sut.CollectFeed(Asset, Feed, "/data", FromMs, ToMs, ct: CancellationToken.None);

        // The last call should be the full collection from discovered start to toMs.
        await _collector.Received().Collect(
            Asset, Feed, "/data", ValidStartMs, ToMs, Arg.Any<CancellationToken>());
    }

    // -------------------------------------------------------------------------
    // 6b. Materializer-only feed (no live collector) still reaches the archive
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CollectFeed_NoLiveCollector_ButMaterializerRegistered_InvokesArchive()
    {
        var materializer = Substitute.For<IArchiveMaterializer>();
        materializer.Exchange.Returns("binance");
        materializer.FeedName.Returns("taker-volume");
        materializer.Supports("perpetual").Returns(true);
        materializer.MaterializeMonth(
                Arg.Any<CollectionAsset>(), Arg.Any<CollectionFeed>(),
                Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ArchiveMonthResult(10, true));

        var coverage = Substitute.For<IMonthCoverageCalculator>();
        coverage.IsMonthCovered(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int>(), Arg.Any<int>(),
                Arg.Any<IReadOnlyList<DataGap>>(), Arg.Any<MonthPartitionRow?>(), Arg.Any<IReadOnlyList<string>?>(),
                Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var archiveBackfill = new ArchiveBackfillService(
            new ArchiveMaterializerRegistry([materializer]),
            coverage,
            Substitute.For<IFeedStatusStore>(),
            Substitute.For<IHistoryIndex>(),
            new CollectionChangeNotifier(),
            new TestClock(new DateTimeOffset(2026, 7, 7, 0, 0, 0, TimeSpan.Zero)),
            NullLogger<ArchiveBackfillService>.Instance);

        // Empty collector list → taker-volume has no live collector.
        var sut = new SymbolCollector(
            [],
            archiveBackfill,
            _index,
            _notifier,
            NullLogger<SymbolCollector>.Instance);

        var takerFeed = CollectionAssets.Feed("taker-volume", "5m");
        var oneClosedMonthEnd = new DateTimeOffset(2020, 2, 1, 0, 0, 0, TimeSpan.Zero)
            .ToUnixTimeMilliseconds();

        await sut.CollectFeed(
            Asset, takerFeed, "/data", FromMs, oneClosedMonthEnd,
            ct: TestContext.Current.CancellationToken);

        // Archive was reached despite no live collector — proves the early-return is gone.
        await materializer.Received().MaterializeMonth(
            Arg.Any<CollectionAsset>(), Arg.Any<CollectionFeed>(),
            Arg.Any<string>(), 2020, 1, Arg.Any<CancellationToken>());
    }

    // -------------------------------------------------------------------------
    // 6c. Unknown feed with no collector AND no materializer no-ops (no throw)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CollectFeed_NoCollector_NoMaterializer_NoOps()
    {
        var unknownFeed = CollectionAssets.Feed("does-not-exist", "5m");

        await _sut.CollectFeed(
            Asset, unknownFeed, "/data", FromMs, ToMs,
            ct: TestContext.Current.CancellationToken);

        await _index.DidNotReceiveWithAnyArgs()
            .SetDiscoveredFirstMonth(default!, default!, default!, default!, default!, TestContext.Current.CancellationToken);
    }

    // -------------------------------------------------------------------------
    // 6d. Per-feed dedup gate (spec §3.4): a concurrent CollectFeed for the SAME
    //     (assetDir, feed, interval) skips instead of doubling the work; a
    //     DIFFERENT feed key proceeds concurrently.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CollectFeed_ConcurrentSameFeed_SecondCallSkips()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _collector.Collect(
                Arg.Any<CollectionAsset>(), Arg.Any<CollectionFeed>(),
                Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                entered.TrySetResult();
                await release.Task;
            });

        var ct = TestContext.Current.CancellationToken;
        var first = _sut.CollectFeed(Asset, Feed, "/data", FromMs, ToMs, ct: ct);
        await entered.Task; // first call is inside the collector, holding the gate

        // Second call for the same (assetDir, feed, interval) must skip, not wait.
        await _sut.CollectFeed(Asset, Feed, "/data", FromMs, ToMs, ct: ct);

        release.TrySetResult();
        await first;

        await _collector.Received(1).Collect(
            Arg.Any<CollectionAsset>(), Arg.Any<CollectionFeed>(),
            Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CollectFeed_ConcurrentDifferentFeedKeys_BothProceed()
    {
        var enteredSignal = new SemaphoreSlim(0);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _collector.Collect(
                Arg.Any<CollectionAsset>(), Arg.Any<CollectionFeed>(),
                Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                enteredSignal.Release();
                await release.Task;
            });

        var ct = TestContext.Current.CancellationToken;
        var feed5m = CollectionAssets.Feed("open-interest", "5m");
        var feed15m = CollectionAssets.Feed("open-interest", "15m"); // different gate key

        var first = _sut.CollectFeed(Asset, feed5m, "/data", FromMs, ToMs, ct: ct);
        await enteredSignal.WaitAsync(ct);

        var second = _sut.CollectFeed(Asset, feed15m, "/data", FromMs, ToMs, ct: ct);
        await enteredSignal.WaitAsync(ct); // second entered while first still holds its own gate

        release.TrySetResult();
        await Task.WhenAll(first, second);

        await _collector.Received(2).Collect(
            Arg.Any<CollectionAsset>(), Arg.Any<CollectionFeed>(),
            Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    // -------------------------------------------------------------------------
    // 7. Month index helpers
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(2020, 1)]
    [InlineData(2020, 8)]
    [InlineData(2025, 12)]
    public void MonthIndex_RoundTrips(int year, int month)
    {
        var ms = new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var idx = SymbolCollector.ToMonthIndex(ms);
        var roundTripped = SymbolCollector.FromMonthIndex(idx);
        Assert.Equal(ms, roundTripped);
    }
}
