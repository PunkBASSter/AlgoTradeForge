using System.Text.Json;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Infrastructure.Storage;
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

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private string AssetDir(string name) => Path.Combine(_tempDir, name);

    private static FeedMetadata ReadFeedsJson(string assetDir)
    {
        var path = Path.Combine(assetDir, "feeds.json");
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<FeedMetadata>(json, JsonOptions)!;
    }

    // -------------------------------------------------------------------------
    // Load_NoFile_ReturnsNull
    // -------------------------------------------------------------------------

    [Fact]
    public void Load_NoFile_ReturnsNull()
    {
        var manager  = new FeedSchemaManager();
        var assetDir = AssetDir("BTCUSDT_Load");

        var result = manager.Load(assetDir);

        Assert.Null(result);
    }

    // -------------------------------------------------------------------------
    // EnsureSchema_NewFile_CreatesFeedsJson
    // -------------------------------------------------------------------------

    [Fact]
    public void EnsureSchema_NewFile_CreatesFeedsJson()
    {
        var manager  = new FeedSchemaManager();
        var assetDir = AssetDir("BTCUSDT_New");
        var columns  = new[] { "rate", "mark" };

        manager.EnsureSchema(assetDir, "funding", "8h", columns);

        var feedsJsonPath = Path.Combine(assetDir, "feeds.json");
        Assert.True(File.Exists(feedsJsonPath));

        var metadata = ReadFeedsJson(assetDir);
        Assert.True(metadata.Feeds.ContainsKey("funding"));

        var def = metadata.Feeds["funding"];
        Assert.Equal("8h",             def.Interval);
        Assert.Equal(columns,          def.Columns);
        Assert.Null(def.AutoApply);
    }

    // -------------------------------------------------------------------------
    // EnsureSchema_ExistingFile_UpdatesFeed
    // -------------------------------------------------------------------------

    [Fact]
    public void EnsureSchema_ExistingFile_UpdatesFeed()
    {
        var manager  = new FeedSchemaManager();
        var assetDir = AssetDir("BTCUSDT_Update");

        // Create initial feed with two columns
        manager.EnsureSchema(assetDir, "funding", "8h",
            columns: ["rate"]);

        // Update the same feed with a different column set
        manager.EnsureSchema(assetDir, "funding", "4h",
            columns: ["rate", "mark", "index"]);

        var metadata = ReadFeedsJson(assetDir);
        Assert.Single(metadata.Feeds);

        var def = metadata.Feeds["funding"];
        Assert.Equal("4h",                          def.Interval);
        Assert.Equal(["rate", "mark", "index"],     def.Columns);
    }

    // -------------------------------------------------------------------------
    // EnsureCandleConfig_NewFile_CreatesCandleSection
    // -------------------------------------------------------------------------

    [Fact]
    public void EnsureCandleConfig_NewFile_CreatesCandleSection()
    {
        var manager  = new FeedSchemaManager();
        var assetDir = AssetDir("ETHUSDT_Candle");

        manager.EnsureCandleConfig(assetDir, decimalDigits: 2, interval: "1h");

        Assert.True(File.Exists(Path.Combine(assetDir, "feeds.json")));

        var metadata = ReadFeedsJson(assetDir);
        Assert.NotNull(metadata.Candles);
        Assert.Equal(100m,    metadata.Candles!.ScaleFactor);
        Assert.Single(metadata.Candles.Intervals);
        Assert.Equal("1h",    metadata.Candles.Intervals[0]);
    }

    // -------------------------------------------------------------------------
    // EnsureCandleConfig_ExistingFile_AddsInterval
    // -------------------------------------------------------------------------

    [Fact]
    public void EnsureCandleConfig_ExistingFile_AddsInterval()
    {
        var manager  = new FeedSchemaManager();
        var assetDir = AssetDir("ETHUSDT_AddInterval");

        manager.EnsureCandleConfig(assetDir, decimalDigits: 2, interval: "1m");
        manager.EnsureCandleConfig(assetDir, decimalDigits: 2, interval: "1d");

        var metadata = ReadFeedsJson(assetDir);
        Assert.NotNull(metadata.Candles);
        Assert.Equal(2, metadata.Candles!.Intervals.Length);
        Assert.Contains("1m", metadata.Candles.Intervals);
        Assert.Contains("1d", metadata.Candles.Intervals);
    }

    // -------------------------------------------------------------------------
    // AtomicWrite_NoPartialFiles
    // -------------------------------------------------------------------------

    [Fact]
    public void AtomicWrite_NoPartialFiles()
    {
        var manager  = new FeedSchemaManager();
        var assetDir = AssetDir("SOLUSDT_Atomic");

        manager.EnsureSchema(assetDir, "funding", "8h", columns: ["rate"]);

        var tmpPath = Path.Combine(assetDir, "feeds.json.tmp");
        Assert.False(File.Exists(tmpPath), "Temporary .tmp file must not remain after successful write.");
    }
}
