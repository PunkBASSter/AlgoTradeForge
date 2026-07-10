using System.Text.Json;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Infrastructure.Index;
using AlgoTradeForge.HistoryLoader.Tests.TestHelpers;
using AlgoTradeForge.HistoryLoader.WebApi.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Archive;

public sealed class CoverageEndpointTests
{
    // Clock pinned well past any 2024 test data so all past months are fully elapsed.
    private static readonly TimeProvider Clock = new TestClock(new DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.Zero));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IOptionsMonitor<HistoryLoaderOptions> Options(string dataRoot = "/test-root")
    {
        var opts = Substitute.For<IOptionsMonitor<HistoryLoaderOptions>>();
        opts.CurrentValue.Returns(new HistoryLoaderOptions { DataRoot = dataRoot });
        return opts;
    }

    // Creates a real SqliteHistoryIndex backed by an isolated temp DB (Pooling=False).
    private static (IHistoryIndex Index, Func<Task> Cleanup) CreateIndex()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"test-coverage-{Guid.NewGuid():N}.sqlite");
        var connStr = $"Data Source={dbPath};Pooling=False";
        var initializer = new HistoryIndexInitializer(dbPath);
        var index = new SqliteHistoryIndex(initializer, connStr);
        return (index, async () =>
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
            await Task.CompletedTask;
        });
    }

    private static string SerManifest(FeedMetadata manifest) =>
        JsonSerializer.Serialize(manifest, ManifestJson.Options);

    private static string SerGaps(IReadOnlyList<DataGap> gaps) =>
        JsonSerializer.Serialize(gaps, ManifestJson.Options);

    private static string SerMonths(string[] months) =>
        JsonSerializer.Serialize(months, ManifestJson.Options);

    // -------------------------------------------------------------------------
    // Validation (422) — NSubstitute; index is never queried.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task UnknownAssetType_Returns422_NotException()
    {
        var index = Substitute.For<IHistoryIndex>();

        var result = await CoverageEndpoints.GetCoverage(
            "binance", "BTCUSDT", "alien",
            Options(), index, Clock, Ct);

        var statusResult = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, statusResult.StatusCode);
    }

    [Theory]
    [InlineData("../evil", "BTCUSDT")]
    [InlineData("bin\\..\\..", "BTCUSDT")]
    [InlineData("binance", "../secrets")]
    [InlineData("binance", "BTC/USDT")]
    public async Task TraversalInExchangeOrSymbol_Returns422_NoIndexTouch(string exchange, string symbol)
    {
        var index = Substitute.For<IHistoryIndex>();

        var result = await CoverageEndpoints.GetCoverage(
            exchange, symbol, AssetTypes.Spot,
            Options(), index, Clock, Ct);

        var statusResult = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, statusResult.StatusCode);
        // Index must never be queried for a rejected request.
        await index.DidNotReceiveWithAnyArgs().GetAsset(default!, default!, Ct);
    }

    // -------------------------------------------------------------------------
    // Interval-less feeds (ticks, funding-rate) — direct index upserts.
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(FeedNames.Ticks)]
    [InlineData(FeedNames.FundingRate)]
    public async Task Coverage_IntervalLessFeed_ReportsCompleteMonths(string feed)
    {
        var (index, cleanup) = CreateIndex();
        try
        {
            await index.UpsertAsset(new AssetIndexRow("binance", "BTCUSDT", "BTCUSDT", AssetTypes.Spot,
                SerManifest(new FeedMetadata())), Ct);

            const long firstTs = 1704067200000L;
            const long lastTs  = 1706745600000L;

            await index.UpsertFeedStatus(new FeedStatusIndexRow(
                "binance", "BTCUSDT", feed, "",
                firstTs, lastTs, 0, "Healthy",
                SerGaps([]),
                SerMonths(["2024-01", "2024-02"])), Ct);

            var result = await CoverageEndpoints.GetCoverage(
                "binance", "BTCUSDT", AssetTypes.Spot,
                Options(), index, Clock, Ct);

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
            await cleanup();
        }
    }

    [Fact]
    public async Task Coverage_NoIntervalLessStatus_OmitsEntry()
    {
        // Ticks/funding-rate omitted when there is no status row (presence = status row only).
        var (index, cleanup) = CreateIndex();
        try
        {
            await index.UpsertAsset(new AssetIndexRow("binance", "BTCUSDT", "BTCUSDT", AssetTypes.Spot,
                SerManifest(new FeedMetadata())), Ct);
            // No status rows → both interval-less feeds are absent.

            var result = await CoverageEndpoints.GetCoverage(
                "binance", "BTCUSDT", AssetTypes.Spot,
                Options(), index, Clock, Ct);

            Assert.Equal(StatusCodes.Status200OK,
                Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
            var json = JsonSerializer.Serialize(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
            using var doc = JsonDocument.Parse(json);
            Assert.Equal(0, doc.RootElement.GetProperty("feeds").GetArrayLength());
        }
        finally
        {
            await cleanup();
        }
    }

    // -------------------------------------------------------------------------
    // candle-ext mirrors candles — index-seeded.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Coverage_CandleExt_MirrorsCandles()
    {
        // candle-ext has no materializer; its covered months must mirror candles for the same
        // interval regardless of candle-ext's own partition completeness.
        var (index, cleanup) = CreateIndex();
        try
        {
            var manifest = new FeedMetadata
            {
                Candles = new CandleConfig { Intervals = ["1h"] },
                Feeds = { [FeedNames.CandleExt] = new FeedDefinition { Interval = "1h" } },
            };
            await index.UpsertAsset(new AssetIndexRow("binance", "BTCUSDT", "BTCUSDT", AssetTypes.Spot,
                SerManifest(manifest)), Ct);

            // March 2024: 31 days × 24 h = 744 rows → covered.
            await index.ReplaceMonths("binance", "BTCUSDT", FeedNames.Candles, "1h",
                [new MonthPartitionRow("2024-03", 744, 10_000, "2025-01-01T00:00:00Z")], Ct);

            // candle-ext presence via month rows; 10 rows would NOT be covered if computed
            // independently, but coverage mirrors candles so it must show 2024-03.
            await index.ReplaceMonths("binance", "BTCUSDT", FeedNames.CandleExt, "1h",
                [new MonthPartitionRow("2024-03", 10, 500, "2025-01-01T00:00:00Z")], Ct);

            var result = await CoverageEndpoints.GetCoverage(
                "binance", "BTCUSDT", AssetTypes.Spot,
                Options(), index, Clock, Ct);

            var json = JsonSerializer.Serialize(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
            using var doc = JsonDocument.Parse(json);
            var feeds = doc.RootElement.GetProperty("feeds").EnumerateArray().ToList();
            var ext = feeds.Single(f => f.GetProperty("feed_name").GetString() == FeedNames.CandleExt);
            var months = ext.GetProperty("covered_months");
            Assert.Equal(1, months.GetArrayLength());
            Assert.Equal("2024-03", months[0].GetString());
        }
        finally
        {
            await cleanup();
        }
    }

    // -------------------------------------------------------------------------
    // Status-less feed (D6 — 12k equity case) — month rows, no status rows.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FeedEntry_NoFeedStatus_TimestampsAreNull()
    {
        // Candles dir exists in index (month row) but no status row → timestamps must be JSON null.
        var (index, cleanup) = CreateIndex();
        try
        {
            var manifest = new FeedMetadata { Candles = new CandleConfig { Intervals = ["1h"] } };
            await index.UpsertAsset(new AssetIndexRow("binance", "BTCUSDT", "BTCUSDT", AssetTypes.Spot,
                SerManifest(manifest)), Ct);

            // Month row exists; no status row → feed entry appears, timestamps null.
            await index.ReplaceMonths("binance", "BTCUSDT", FeedNames.Candles, "1h",
                [new MonthPartitionRow("2024-03", 10, 500, "2025-01-01T00:00:00Z")], Ct);

            var result = await CoverageEndpoints.GetCoverage(
                "binance", "BTCUSDT", AssetTypes.Spot,
                Options(), index, Clock, Ct);

            Assert.Equal(StatusCodes.Status200OK,
                Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
            var json = JsonSerializer.Serialize(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
            using var doc = JsonDocument.Parse(json);
            var feeds = doc.RootElement.GetProperty("feeds");
            Assert.Equal(1, feeds.GetArrayLength());
            var feed = feeds[0];
            Assert.Equal(JsonValueKind.Null, feed.GetProperty("first_timestamp").ValueKind);
            Assert.Equal(JsonValueKind.Null, feed.GetProperty("last_timestamp").ValueKind);
        }
        finally
        {
            await cleanup();
        }
    }

    [Fact]
    public async Task StatusLess_EquityAsset_FeedPresent_CoveredMonthsFromRowCountAlone()
    {
        // Spec D6 (the 12k-equity case): equity-shaped asset populated from static CSV partitions
        // has month rows in the index but NO feed_status rows. The feed entry must appear, covered
        // months must be computed from row counts alone, and timestamps must be null.
        var (index, cleanup) = CreateIndex();
        try
        {
            var manifest = new FeedMetadata { Candles = new CandleConfig { Intervals = ["1h"] } };
            await index.UpsertAsset(new AssetIndexRow("nasdaq", "AAPL", "AAPL", AssetTypes.Equity,
                SerManifest(manifest)), Ct);

            // January 2024: 31 × 24 = 744 rows → covered (no gaps, no listing clamp, nowMs >> month end).
            await index.ReplaceMonths("nasdaq", "AAPL", FeedNames.Candles, "1h",
                [new MonthPartitionRow("2024-01", 744, 12_000, "2025-01-01T00:00:00Z")], Ct);

            var result = await CoverageEndpoints.GetCoverage(
                "nasdaq", "AAPL", AssetTypes.Equity,
                Options(), index, Clock, Ct);

            Assert.Equal(StatusCodes.Status200OK,
                Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
            var json = JsonSerializer.Serialize(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
            using var doc = JsonDocument.Parse(json);
            var feeds = doc.RootElement.GetProperty("feeds");
            Assert.Equal(1, feeds.GetArrayLength());
            var entry = feeds[0];
            Assert.Equal(FeedNames.Candles, entry.GetProperty("feed_name").GetString());
            // Covered months computed from row count alone (no status row).
            var months = entry.GetProperty("covered_months");
            Assert.Equal(1, months.GetArrayLength());
            Assert.Equal("2024-01", months[0].GetString());
            // No status row → both timestamps are JSON null.
            Assert.Equal(JsonValueKind.Null, entry.GetProperty("first_timestamp").ValueKind);
            Assert.Equal(JsonValueKind.Null, entry.GetProperty("last_timestamp").ValueKind);
        }
        finally
        {
            await cleanup();
        }
    }

    // -------------------------------------------------------------------------
    // Gap-credit: month with a DataGap is covered via credit rows.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GapCredit_IncompleteMonthWithGap_Covered()
    {
        // March 2024: 744 expected (31 d × 24 h). 720 actual rows + DataGap crediting 24 rows → covered.
        // Gap: fromMs = origin+100h, toMs = fromMs+25h → credit = (25h - 1h) / 1h = 24.
        var (index, cleanup) = CreateIndex();
        try
        {
            var manifest = new FeedMetadata { Candles = new CandleConfig { Intervals = ["1h"] } };
            await index.UpsertAsset(new AssetIndexRow("binance", "BTCUSDT", "BTCUSDT", AssetTypes.Spot,
                SerManifest(manifest)), Ct);

            var originMs = new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
            var gapFrom  = originMs + 100L * 3_600_000;
            var gapTo    = gapFrom  + 25L  * 3_600_000;
            var gaps = new[] { new DataGap { FromMs = gapFrom, ToMs = gapTo } };

            await index.UpsertFeedStatus(new FeedStatusIndexRow(
                "binance", "BTCUSDT", FeedNames.Candles, "1h",
                originMs, originMs + 719L * 3_600_000, 720, "Degraded",
                SerGaps(gaps),
                SerMonths([])), Ct);

            await index.ReplaceMonths("binance", "BTCUSDT", FeedNames.Candles, "1h",
                [new MonthPartitionRow("2024-03", 720, 8_000, "2025-01-01T00:00:00Z")], Ct);

            var result = await CoverageEndpoints.GetCoverage(
                "binance", "BTCUSDT", AssetTypes.Spot,
                Options(), index, Clock, Ct);

            var json = JsonSerializer.Serialize(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
            using var doc = JsonDocument.Parse(json);
            var feeds = doc.RootElement.GetProperty("feeds");
            Assert.Equal(1, feeds.GetArrayLength());
            var months = feeds[0].GetProperty("covered_months");
            Assert.Equal(1, months.GetArrayLength());
            Assert.Equal("2024-03", months[0].GetString());
        }
        finally
        {
            await cleanup();
        }
    }
}
