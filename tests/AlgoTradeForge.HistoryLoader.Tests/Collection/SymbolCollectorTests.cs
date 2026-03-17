using System.Net;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Collection;

public sealed class SymbolCollectorTests
{
    private readonly IFeedCollector _collector = Substitute.For<IFeedCollector>();
    private readonly ISettingsWriter _settingsWriter = Substitute.For<ISettingsWriter>();
    private readonly SymbolCollector _sut;

    private static readonly AssetCollectionConfig Asset = new()
    {
        Symbol = "BTCUSDT",
        Type = "perpetual",
        HistoryStart = new DateOnly(2020, 1, 1),
    };

    private static readonly FeedCollectionConfig Feed = new()
    {
        Name = "open-interest",
        Interval = "5m",
    };

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

        _sut = new SymbolCollector(
            [_collector],
            _settingsWriter,
            NullLogger<SymbolCollector>.Instance);
    }

    /// <summary>
    /// Sets up the mock to succeed when fromMs >= validStart, fail with date-range
    /// error otherwise. This simulates a Binance endpoint that only has data from
    /// a certain date onward.
    /// </summary>
    private void SetupDateThreshold(long validStart)
    {
        _collector.CollectAsync(
                Arg.Any<AssetCollectionConfig>(), Arg.Any<FeedCollectionConfig>(),
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
    // 1. Date-range 400 → binary search finds valid start, persists
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CollectFeedAsync_DateRange400_BinarySearchFindsAndPersists()
    {
        SetupDateThreshold(ValidStartMs);

        await _sut.CollectFeedAsync(Asset, Feed, "/data", FromMs, ToMs, CancellationToken.None);

        // Should persist August 2020 as the discovered start.
        _settingsWriter.Received(1).UpdateFeedHistoryStart(
            "BTCUSDT", "perpetual", "open-interest", "5m",
            new DateOnly(2020, 8, 1));
    }

    // -------------------------------------------------------------------------
    // 1b. Binary search uses O(log n) probes, not O(n)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CollectFeedAsync_BinarySearch_UsesLogarithmicProbes()
    {
        int callCount = 0;
        _collector.CollectAsync(
                Arg.Any<AssetCollectionConfig>(), Arg.Any<FeedCollectionConfig>(),
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

        await _sut.CollectFeedAsync(Asset, Feed, "/data", FromMs, ToMs, CancellationToken.None);

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
    public async Task CollectFeedAsync_InvalidSymbol_DoesNotRetry()
    {
        _collector.CollectAsync(
                Arg.Any<AssetCollectionConfig>(), Arg.Any<FeedCollectionConfig>(),
                Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Throws(new DataSourceApiException(-1121, "Invalid symbol.", HttpStatusCode.BadRequest));

        await _sut.CollectFeedAsync(Asset, Feed, "/data", FromMs, ToMs, CancellationToken.None);

        await _collector.Received(1).CollectAsync(
            Arg.Any<AssetCollectionConfig>(), Arg.Any<FeedCollectionConfig>(),
            Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
        _settingsWriter.DidNotReceiveWithAnyArgs()
            .UpdateFeedHistoryStart(default!, default!, default!, default!, default);
    }

    // -------------------------------------------------------------------------
    // 2b. Endpoint maintenance → does not retry
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CollectFeedAsync_EndpointMaintenance_DoesNotRetry()
    {
        _collector.CollectAsync(
                Arg.Any<AssetCollectionConfig>(), Arg.Any<FeedCollectionConfig>(),
                Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Throws(new DataSourceApiException(
                -1, "The endpoint has been out of maintenance", HttpStatusCode.BadRequest));

        await _sut.CollectFeedAsync(Asset, Feed, "/data", FromMs, ToMs, CancellationToken.None);

        await _collector.Received(1).CollectAsync(
            Arg.Any<AssetCollectionConfig>(), Arg.Any<FeedCollectionConfig>(),
            Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
        _settingsWriter.DidNotReceiveWithAnyArgs()
            .UpdateFeedHistoryStart(default!, default!, default!, default!, default);
    }

    // -------------------------------------------------------------------------
    // 3. Plain HttpRequestException 400 → does not retry
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CollectFeedAsync_PlainHttp400_DoesNotRetry()
    {
        _collector.CollectAsync(
                Arg.Any<AssetCollectionConfig>(), Arg.Any<FeedCollectionConfig>(),
                Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("Bad Request", null, HttpStatusCode.BadRequest));

        await _sut.CollectFeedAsync(Asset, Feed, "/data", FromMs, ToMs, CancellationToken.None);

        await _collector.Received(1).CollectAsync(
            Arg.Any<AssetCollectionConfig>(), Arg.Any<FeedCollectionConfig>(),
            Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
        _settingsWriter.DidNotReceiveWithAnyArgs()
            .UpdateFeedHistoryStart(default!, default!, default!, default!, default);
    }

    // -------------------------------------------------------------------------
    // 4. All months fail → gives up, does not persist
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CollectFeedAsync_AllMonthsFail_GivesUp()
    {
        _collector.CollectAsync(
                Arg.Any<AssetCollectionConfig>(), Arg.Any<FeedCollectionConfig>(),
                Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Throws(new DataSourceApiException(
                -1, "Invalid period.", HttpStatusCode.BadRequest, isDateRangeError: true));

        await _sut.CollectFeedAsync(Asset, Feed, "/data", FromMs, ToMs, CancellationToken.None);

        _settingsWriter.DidNotReceiveWithAnyArgs()
            .UpdateFeedHistoryStart(default!, default!, default!, default!, default);
    }

    // -------------------------------------------------------------------------
    // 5. Success on first try → no probing, does not persist
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CollectFeedAsync_SuccessOnFirstTry_DoesNotPersist()
    {
        _collector.CollectAsync(
                Arg.Any<AssetCollectionConfig>(), Arg.Any<FeedCollectionConfig>(),
                Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _sut.CollectFeedAsync(Asset, Feed, "/data", FromMs, ToMs, CancellationToken.None);

        await _collector.Received(1).CollectAsync(
            Arg.Any<AssetCollectionConfig>(), Arg.Any<FeedCollectionConfig>(),
            Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
        _settingsWriter.DidNotReceiveWithAnyArgs()
            .UpdateFeedHistoryStart(default!, default!, default!, default!, default);
    }

    // -------------------------------------------------------------------------
    // 6. Final full collection uses discovered start, not toMs
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CollectFeedAsync_FinalCollection_UsesDiscoveredStart()
    {
        SetupDateThreshold(ValidStartMs);

        await _sut.CollectFeedAsync(Asset, Feed, "/data", FromMs, ToMs, CancellationToken.None);

        // The last call should be the full collection from discovered start to toMs.
        await _collector.Received().CollectAsync(
            Asset, Feed, "/data", ValidStartMs, ToMs, Arg.Any<CancellationToken>());
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
