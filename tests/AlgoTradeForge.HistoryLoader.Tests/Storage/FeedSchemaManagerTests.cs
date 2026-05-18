using System.Text.Json;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Infrastructure.Storage;
using AlgoTradeForge.Infrastructure.IO;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Storage;

public sealed class FeedSchemaManagerTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), $"FeedSchemaManagerTests_{Guid.NewGuid():N}");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string AssetDir(string name) => Path.Combine(_tempDir, name);

    private static FeedMetadata ReadFeedsJson(string assetDir)
    {
        var path = Path.Combine(assetDir, "feeds.json");
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<FeedMetadata>(json, JsonOptions)!;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Load_NoFile_ReturnsNull()
    {
        var manager  = new FeedSchemaManager(new LocalFileStorage());
        var assetDir = AssetDir("BTCUSDT_Load");

        var result = await manager.Load(assetDir, Ct);

        Assert.Null(result);
    }

    [Fact]
    public async Task EnsureSchema_NewFile_CreatesFeedsJson()
    {
        var manager  = new FeedSchemaManager(new LocalFileStorage());
        var assetDir = AssetDir("BTCUSDT_New");
        var columns  = new[] { "rate", "mark" };

        await manager.EnsureSchema(assetDir, "funding", "8h", columns, ct: Ct);

        var feedsJsonPath = Path.Combine(assetDir, "feeds.json");
        Assert.True(File.Exists(feedsJsonPath));

        var metadata = ReadFeedsJson(assetDir);
        Assert.True(metadata.Feeds.ContainsKey("funding"));

        var def = metadata.Feeds["funding"];
        Assert.Equal("8h",             def.Interval);
        Assert.Equal(columns,          def.Columns);
        Assert.Null(def.AutoApply);
    }

    [Fact]
    public async Task EnsureSchema_ExistingFile_UpdatesFeed()
    {
        var manager  = new FeedSchemaManager(new LocalFileStorage());
        var assetDir = AssetDir("BTCUSDT_Update");

        await manager.EnsureSchema(assetDir, "funding", "8h",
            columns: ["rate"], ct: Ct);

        await manager.EnsureSchema(assetDir, "funding", "4h",
            columns: ["rate", "mark", "index"], ct: Ct);

        var metadata = ReadFeedsJson(assetDir);
        Assert.Single(metadata.Feeds);

        var def = metadata.Feeds["funding"];
        Assert.Equal("4h",                          def.Interval);
        Assert.Equal(["rate", "mark", "index"],     def.Columns);
    }

    [Fact]
    public async Task EnsureCandleConfig_NewFile_CreatesCandleSection()
    {
        var manager  = new FeedSchemaManager(new LocalFileStorage());
        var assetDir = AssetDir("ETHUSDT_Candle");

        await manager.EnsureCandleConfig(assetDir, decimalDigits: 2, interval: "1h", ct: Ct);

        Assert.True(File.Exists(Path.Combine(assetDir, "feeds.json")));

        var metadata = ReadFeedsJson(assetDir);
        Assert.NotNull(metadata.Candles);
        Assert.Equal(100m,    metadata.Candles!.ScaleFactor);
        Assert.Single(metadata.Candles.Intervals);
        Assert.Equal("1h",    metadata.Candles.Intervals[0]);
    }

    [Fact]
    public async Task EnsureCandleConfig_ExistingFile_AddsInterval()
    {
        var manager  = new FeedSchemaManager(new LocalFileStorage());
        var assetDir = AssetDir("ETHUSDT_AddInterval");

        await manager.EnsureCandleConfig(assetDir, decimalDigits: 2, interval: "1m", ct: Ct);
        await manager.EnsureCandleConfig(assetDir, decimalDigits: 2, interval: "1d", ct: Ct);

        var metadata = ReadFeedsJson(assetDir);
        Assert.NotNull(metadata.Candles);
        Assert.Equal(2, metadata.Candles!.Intervals.Length);
        Assert.Contains("1m", metadata.Candles.Intervals);
        Assert.Contains("1d", metadata.Candles.Intervals);
    }

    [Fact]
    public async Task AtomicWrite_NoPartialFiles()
    {
        var manager  = new FeedSchemaManager(new LocalFileStorage());
        var assetDir = AssetDir("SOLUSDT_Atomic");

        await manager.EnsureSchema(assetDir, "funding", "8h", columns: ["rate"], ct: Ct);

        var tmpPath = Path.Combine(assetDir, "feeds.json.tmp");
        Assert.False(File.Exists(tmpPath), "Temporary .tmp file must not remain after successful write.");
    }

    [Fact]
    public async Task SetAutoApplyParams_FeedMissing_ReturnsFalse()
    {
        var manager = new FeedSchemaManager(new LocalFileStorage());
        var assetDir = AssetDir("BTCUSDT_UpdateMissing");

        var updated = await manager.SetAutoApplyParams(assetDir, "funding-rate", 0.03, -0.03, 8, false, Ct);

        Assert.False(updated);
    }

    [Fact]
    public async Task SetAutoApplyParams_AutoApplyMissing_ReturnsFalse()
    {
        var manager = new FeedSchemaManager(new LocalFileStorage());
        var assetDir = AssetDir("BTCUSDT_NoAutoApply");

        await manager.EnsureSchema(assetDir, "funding-rate", "", columns: ["rate", "mark_price"], ct: Ct);

        var updated = await manager.SetAutoApplyParams(assetDir, "funding-rate", 0.03, -0.03, 8, false, Ct);

        Assert.False(updated);
    }

    [Fact]
    public async Task SetAutoApplyParams_FeedWithAutoApply_ReplacesParams()
    {
        var manager = new FeedSchemaManager(new LocalFileStorage());
        var assetDir = AssetDir("BTCUSDT_Update");

        await manager.EnsureSchema(
            assetDir, "funding-rate", "",
            columns: ["rate", "mark_price"],
            autoApply: new AlgoTradeForge.HistoryLoader.Application.Abstractions.AutoApplySpec(
                "FundingRate", "rate"),
            ct: Ct);

        var updated = await manager.SetAutoApplyParams(
            assetDir, "funding-rate",
            cap: 0.0300, floor: -0.0300, intervalHours: 8, disclaimer: false, ct: Ct);

        Assert.True(updated);

        var metadata = ReadFeedsJson(assetDir);
        var feed = metadata.Feeds["funding-rate"];
        Assert.NotNull(feed.AutoApply);
        Assert.Equal("FundingRate", feed.AutoApply.Type);
        Assert.Equal("rate", feed.AutoApply.RateColumn);
        Assert.Equal(0.0300, feed.AutoApply.Cap);
        Assert.Equal(-0.0300, feed.AutoApply.Floor);
        Assert.Equal(8, feed.AutoApply.IntervalHours);
        Assert.False(feed.AutoApply.Disclaimer);

        Assert.Equal(["rate", "mark_price"], feed.Columns);
    }

    [Fact]
    public async Task SetAutoApplyParams_NullArgs_ClearExistingValues()
    {
        var manager = new FeedSchemaManager(new LocalFileStorage());
        var assetDir = AssetDir("BTCUSDT_UpdateClears");

        await manager.EnsureSchema(
            assetDir, "funding-rate", "",
            columns: ["rate", "mark_price"],
            autoApply: new AlgoTradeForge.HistoryLoader.Application.Abstractions.AutoApplySpec(
                "FundingRate", "rate"),
            ct: Ct);

        await manager.SetAutoApplyParams(assetDir, "funding-rate", 0.03, -0.03, 8, true, Ct);

        await manager.SetAutoApplyParams(
            assetDir, "funding-rate",
            cap: null, floor: null, intervalHours: null, disclaimer: null, ct: Ct);

        var metadata = ReadFeedsJson(assetDir);
        var feed = metadata.Feeds["funding-rate"];
        Assert.NotNull(feed.AutoApply);
        Assert.Null(feed.AutoApply.Cap);
        Assert.Null(feed.AutoApply.Floor);
        Assert.Null(feed.AutoApply.IntervalHours);
        Assert.Null(feed.AutoApply.Disclaimer);
    }

    [Fact]
    public async Task SetAutoApplyParams_PreservesOtherFeeds()
    {
        var manager = new FeedSchemaManager(new LocalFileStorage());
        var assetDir = AssetDir("BTCUSDT_UpdatePreserves");

        await manager.EnsureSchema(assetDir, "open-interest", "5m", columns: ["oi", "oi_usd"], ct: Ct);
        await manager.EnsureSchema(
            assetDir, "funding-rate", "",
            columns: ["rate", "mark_price"],
            autoApply: new AlgoTradeForge.HistoryLoader.Application.Abstractions.AutoApplySpec(
                "FundingRate", "rate"),
            ct: Ct);

        await manager.SetAutoApplyParams(assetDir, "funding-rate", 0.03, -0.03, 8, false, Ct);

        var metadata = ReadFeedsJson(assetDir);
        Assert.Equal(2, metadata.Feeds.Count);
        Assert.True(metadata.Feeds.ContainsKey("open-interest"));
        Assert.Equal(["oi", "oi_usd"], metadata.Feeds["open-interest"].Columns);
    }

    [Fact]
    public async Task ConcurrentEnsureSchema_DifferentFeeds_BothPresent()
    {
        var manager  = new FeedSchemaManager(new LocalFileStorage());
        var assetDir = AssetDir("BTCUSDT_Concurrent");

        var barrier = new Barrier(2);
        var ct = TestContext.Current.CancellationToken;

        var t1 = Task.Run(async () =>
        {
            barrier.SignalAndWait(ct);
            await manager.EnsureSchema(assetDir, "funding", "8h", columns: ["rate"], ct: ct);
        }, ct);
        var t2 = Task.Run(async () =>
        {
            barrier.SignalAndWait(ct);
            await manager.EnsureSchema(assetDir, "open-interest", "5m", columns: ["oi", "sumOi"], ct: ct);
        }, ct);

        await Task.WhenAll(t1, t2);

        var metadata = ReadFeedsJson(assetDir);
        Assert.Equal(2, metadata.Feeds.Count);
        Assert.True(metadata.Feeds.ContainsKey("funding"));
        Assert.True(metadata.Feeds.ContainsKey("open-interest"));
    }
}
