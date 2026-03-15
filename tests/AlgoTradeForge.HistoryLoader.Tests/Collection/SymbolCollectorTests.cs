using System.Net;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using Microsoft.Extensions.Logging;
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

    private static readonly long ToMs = new DateTimeOffset(2020, 6, 1, 0, 0, 0, TimeSpan.Zero)
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

    // -------------------------------------------------------------------------
    // 1. Date-range 400 → advances until success, persists discovered date
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CollectFeedAsync_DateRange400_AdvancesAndPersists()
    {
        int callCount = 0;
        _collector.CollectAsync(
                Arg.Any<AssetCollectionConfig>(), Arg.Any<FeedCollectionConfig>(),
                Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                callCount++;
                if (callCount <= 2)
                    throw new DataSourceApiException(-1, "Invalid period.", HttpStatusCode.BadRequest, isDateRangeError: true);
                return Task.CompletedTask;
            });

        await _sut.CollectFeedAsync(Asset, Feed, "/data", FromMs, ToMs, CancellationToken.None);

        // Should have been called 3 times (2 failures + 1 success).
        Assert.Equal(3, callCount);

        // Should persist the discovered date (March 1, 2020 — two months advanced from Jan 1).
        _settingsWriter.Received(1).UpdateFeedHistoryStart(
            "BTCUSDT", "perpetual", "open-interest", "5m",
            new DateOnly(2020, 3, 1));
    }

    // -------------------------------------------------------------------------
    // 1b. startTime invalid (-1130) → also a date-range error, advances
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CollectFeedAsync_StartTimeInvalid_AdvancesAndPersists()
    {
        int callCount = 0;
        _collector.CollectAsync(
                Arg.Any<AssetCollectionConfig>(), Arg.Any<FeedCollectionConfig>(),
                Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                callCount++;
                if (callCount <= 1)
                    throw new DataSourceApiException(-1130, "parameter 'startTime' is invalid.", HttpStatusCode.BadRequest, isDateRangeError: true);
                return Task.CompletedTask;
            });

        await _sut.CollectFeedAsync(Asset, Feed, "/data", FromMs, ToMs, CancellationToken.None);

        Assert.Equal(2, callCount);
        _settingsWriter.Received(1).UpdateFeedHistoryStart(
            "BTCUSDT", "perpetual", "open-interest", "5m",
            new DateOnly(2020, 2, 1));
    }

    // -------------------------------------------------------------------------
    // 2. Non-date-range API error → does not retry
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
    // 2b. Endpoint maintenance error → does not retry (not a date-range issue)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CollectFeedAsync_EndpointMaintenance_DoesNotRetry()
    {
        _collector.CollectAsync(
                Arg.Any<AssetCollectionConfig>(), Arg.Any<FeedCollectionConfig>(),
                Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Throws(new DataSourceApiException(-1, "The endpoint has been out of maintenance", HttpStatusCode.BadRequest));

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
    // 4. Exhausts max advances → gives up
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CollectFeedAsync_ExhaustsMaxAdvances_GivesUp()
    {
        _collector.CollectAsync(
                Arg.Any<AssetCollectionConfig>(), Arg.Any<FeedCollectionConfig>(),
                Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Throws(new DataSourceApiException(-1, "Invalid period.", HttpStatusCode.BadRequest, isDateRangeError: true));

        await _sut.CollectFeedAsync(Asset, Feed, "/data", FromMs, ToMs, CancellationToken.None);

        // 1 initial + 24 advances = 25 total calls.
        await _collector.Received(SymbolCollector.MaxDateAdvances + 1).CollectAsync(
            Arg.Any<AssetCollectionConfig>(), Arg.Any<FeedCollectionConfig>(),
            Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
        _settingsWriter.DidNotReceiveWithAnyArgs()
            .UpdateFeedHistoryStart(default!, default!, default!, default!, default);
    }

    // -------------------------------------------------------------------------
    // 5. Success on first try → does not persist
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
    // 6. AdvanceOneMonth snaps to 1st of next month
    // -------------------------------------------------------------------------

    [Fact]
    public void AdvanceOneMonth_SnapsToFirstOfNextMonth()
    {
        // 2020-01-15 12:30:00 UTC
        var ms = new DateTimeOffset(2020, 1, 15, 12, 30, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var advanced = SymbolCollector.AdvanceOneMonth(ms);
        var dt = DateTimeOffset.FromUnixTimeMilliseconds(advanced).UtcDateTime;

        Assert.Equal(new DateTime(2020, 2, 1, 0, 0, 0, DateTimeKind.Utc), dt);
    }
}
