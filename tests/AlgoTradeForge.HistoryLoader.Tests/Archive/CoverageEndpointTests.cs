using System.Text.Json;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Archive;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.WebApi.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Archive;

public sealed class CoverageEndpointTests
{
    private static (IOptionsMonitor<HistoryLoaderOptions>, ISchemaManager, IFeedStatusStore, IMonthCoverageCalculator) BuildDeps()
    {
        var options = Substitute.For<IOptionsMonitor<HistoryLoaderOptions>>();
        options.CurrentValue.Returns(new HistoryLoaderOptions());
        return (options, Substitute.For<ISchemaManager>(), Substitute.For<IFeedStatusStore>(), Substitute.For<IMonthCoverageCalculator>());
    }

    [Fact]
    public async Task UnknownAssetType_Returns422_NotException()
    {
        var (options, schema, status, coverage) = BuildDeps();

        var result = await CoverageEndpoints.GetCoverage(
            "binance", "BTCUSDT", "alien",
            options, schema, status, coverage,
            TestContext.Current.CancellationToken);

        var statusResult = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, statusResult.StatusCode);
    }

    [Theory]
    [InlineData("../evil", "BTCUSDT")]
    [InlineData("bin\\..\\..", "BTCUSDT")]
    [InlineData("binance", "../secrets")]
    [InlineData("binance", "BTC/USDT")]
    public async Task TraversalInExchangeOrSymbol_Returns422_NoFilesystemTouch(string exchange, string symbol)
    {
        var (options, schema, status, coverage) = BuildDeps();

        var result = await CoverageEndpoints.GetCoverage(
            exchange, symbol, AssetTypes.Spot,
            options, schema, status, coverage,
            TestContext.Current.CancellationToken);

        var statusResult = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, statusResult.StatusCode);
        // Schema must never be queried (no filesystem touch).
        await schema.DidNotReceiveWithAnyArgs().Load(default!, default!);
    }

    [Theory]
    [InlineData(FeedNames.Ticks)]
    [InlineData(FeedNames.FundingRate)]
    public async Task Coverage_IntervalLessFeed_ReportsCompleteMonths(string feed)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var feedDir = Path.Combine(tempRoot, "binance", "BTCUSDT", feed);
        Directory.CreateDirectory(feedDir);
        try
        {
            var (_, schema, statusStore, coverage) = BuildDeps();
            var options = Substitute.For<IOptionsMonitor<HistoryLoaderOptions>>();
            options.CurrentValue.Returns(new HistoryLoaderOptions { DataRoot = tempRoot });

            schema.Load(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<FeedMetadata?>(new FeedMetadata()));

            const long firstTs = 1704067200000L;
            const long lastTs  = 1706745600000L;

            statusStore.Load(Arg.Any<string>(), feed, "", Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<FeedStatus?>(new FeedStatus
                {
                    FeedName = feed,
                    Interval = "",
                    CompleteMonths = ["2024-01", "2024-02"],
                    FirstTimestamp = firstTs,
                    LastTimestamp = lastTs,
                }));

            var result = await CoverageEndpoints.GetCoverage(
                "binance", "BTCUSDT", AssetTypes.Spot,
                options, schema, statusStore, coverage,
                TestContext.Current.CancellationToken);

            Assert.Equal(StatusCodes.Status200OK,
                Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
            var json = JsonSerializer.Serialize(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
            using var doc = JsonDocument.Parse(json);
            var feeds = doc.RootElement.GetProperty("feeds");
            Assert.Equal(1, feeds.GetArrayLength());
            var entry = feeds[0];
            Assert.Equal(feed, entry.GetProperty("feed_name").GetString());
            Assert.Equal("", entry.GetProperty("interval").GetString());
            var months = entry.GetProperty("covered_months");
            Assert.Equal(2, months.GetArrayLength());
            Assert.Equal("2024-01", months[0].GetString());
            Assert.Equal("2024-02", months[1].GetString());
            Assert.Equal(firstTs, entry.GetProperty("first_timestamp").GetInt64());
            Assert.Equal(lastTs, entry.GetProperty("last_timestamp").GetInt64());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Coverage_NoIntervalLessStatus_OmitsEntry()
    {
        // ticks dir exists + status null → omit; funding-rate dir absent → also omit.
        var tempRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var ticksDir = Path.Combine(tempRoot, "binance", "BTCUSDT", FeedNames.Ticks);
        Directory.CreateDirectory(ticksDir);
        try
        {
            var (_, schema, statusStore, coverage) = BuildDeps();
            var options = Substitute.For<IOptionsMonitor<HistoryLoaderOptions>>();
            options.CurrentValue.Returns(new HistoryLoaderOptions { DataRoot = tempRoot });

            schema.Load(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<FeedMetadata?>(new FeedMetadata()));

            // statusStore.Load returns null by default (NSubstitute); omit required.

            var result = await CoverageEndpoints.GetCoverage(
                "binance", "BTCUSDT", AssetTypes.Spot,
                options, schema, statusStore, coverage,
                TestContext.Current.CancellationToken);

            Assert.Equal(StatusCodes.Status200OK,
                Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
            var json = JsonSerializer.Serialize(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
            using var doc = JsonDocument.Parse(json);
            var feeds = doc.RootElement.GetProperty("feeds");
            Assert.Equal(0, feeds.GetArrayLength());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task FeedEntry_NoFeedStatus_TimestampsAreNull()
    {
        // Arrange: a manifest with one candle interval, feed dir exists, no FeedStatus on disk.
        var tempRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var feedDir = Path.Combine(tempRoot, "binance", "BTCUSDT", "candles");
        Directory.CreateDirectory(feedDir);
        try
        {
            var (_, schema, status, coverage) = BuildDeps();
            var options = Substitute.For<IOptionsMonitor<HistoryLoaderOptions>>();
            options.CurrentValue.Returns(new HistoryLoaderOptions { DataRoot = tempRoot });

            schema.Load(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<FeedMetadata?>(new FeedMetadata
                {
                    Candles = new CandleConfig { Intervals = ["1h"] },
                }));

            // feedStatusStore.Load returns null by default (NSubstitute default for Task<T?> = null).

            // Act
            var result = await CoverageEndpoints.GetCoverage(
                "binance", "BTCUSDT", AssetTypes.Spot,
                options, schema, status, coverage,
                TestContext.Current.CancellationToken);

            // Assert: 200 OK, one feed entry, timestamps are JSON null.
            Assert.Equal(StatusCodes.Status200OK, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
            var valueResult = Assert.IsAssignableFrom<IValueHttpResult>(result);
            var json = JsonSerializer.Serialize(valueResult.Value);
            using var doc = JsonDocument.Parse(json);
            var feeds = doc.RootElement.GetProperty("feeds");
            Assert.Equal(1, feeds.GetArrayLength());
            var feed = feeds[0];
            Assert.Equal(JsonValueKind.Null, feed.GetProperty("first_timestamp").ValueKind);
            Assert.Equal(JsonValueKind.Null, feed.GetProperty("last_timestamp").ValueKind);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }
}
