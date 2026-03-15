using System.Text.Json;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Infrastructure.History;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AlgoTradeForge.Infrastructure.Tests.History;

public class FeedContextBuilderTests : IDisposable
{
    private readonly string _testDataRoot;
    private readonly CsvFeedSeriesLoader _feedSeriesLoader = new();  // concrete for integration testing
    private readonly FeedContextBuilder _builder;

    public FeedContextBuilderTests()
    {
        _testDataRoot = Path.Combine(Path.GetTempPath(), $"FeedCtxBuilder_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDataRoot);
        _builder = new FeedContextBuilder(_feedSeriesLoader, NullLogger<FeedContextBuilder>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDataRoot))
            Directory.Delete(_testDataRoot, recursive: true);
    }

    private static long Ts(int year, int month, int day, int hour = 0) =>
        new DateTimeOffset(year, month, day, hour, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private void WriteFeedsJson(string exchange, string assetDir, object metadata)
    {
        var dir = Path.Combine(_testDataRoot, exchange, assetDir);
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(dir, "feeds.json"), json);
    }

    private void WriteFeedCsv(string exchange, string assetDir, string feedName,
        int year, int month, string? interval, string header, string[] rows)
    {
        var dir = Path.Combine(_testDataRoot, exchange, assetDir, feedName);
        Directory.CreateDirectory(dir);
        var fileName = string.IsNullOrEmpty(interval)
            ? $"{year}-{month:D2}.csv"
            : $"{year}-{month:D2}_{interval}.csv";
        var lines = new List<string> { header };
        lines.AddRange(rows);
        File.WriteAllText(Path.Combine(dir, fileName), string.Join(Environment.NewLine, lines));
    }

    [Fact]
    public void Build_NoFeedsJson_ReturnsNull()
    {
        var asset = CryptoPerpetualAsset.Create("BTCUSDT", "Binance", 2);
        var result = _builder.Build(_testDataRoot, asset, new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31));
        Assert.Null(result);
    }

    [Fact]
    public void Build_FeedsJsonWithNoData_ReturnsNull()
    {
        var asset = CryptoPerpetualAsset.Create("BTCUSDT", "Binance", 2);
        var assetDir = AssetDirectoryName.From(asset); // BTCUSDT_fut

        WriteFeedsJson("Binance", assetDir, new
        {
            Feeds = new Dictionary<string, object>
            {
                ["funding_rate"] = new { Interval = "8h", Columns = new[] { "rate" } }
            }
        });
        // No CSV files written

        var result = _builder.Build(_testDataRoot, asset, new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31));
        Assert.Null(result);
    }

    [Fact]
    public void Build_WithFeedData_RegistersFeed()
    {
        var asset = CryptoPerpetualAsset.Create("BTCUSDT", "Binance", 2);
        var assetDir = AssetDirectoryName.From(asset); // BTCUSDT_fut

        WriteFeedsJson("Binance", assetDir, new
        {
            Feeds = new Dictionary<string, object>
            {
                ["funding_rate"] = new { Interval = "8h", Columns = new[] { "rate" } }
            }
        });

        WriteFeedCsv("Binance", assetDir, "funding_rate", 2024, 1, "8h",
            "ts,rate",
            [$"{Ts(2024,1,1,0)},0.0001", $"{Ts(2024,1,1,8)},0.00015"]);

        var result = _builder.Build(_testDataRoot, asset, new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31));

        Assert.NotNull(result);
        var schema = result!.GetSchema("funding_rate");
        Assert.Equal("funding_rate", schema.FeedKey);
        Assert.Single(schema.ColumnNames);
        Assert.Equal("rate", schema.ColumnNames[0]);
        Assert.Null(schema.AutoApply);
    }

    [Fact]
    public void Build_WithAutoApply_SetsAutoApplyConfig()
    {
        var asset = CryptoPerpetualAsset.Create("BTCUSDT", "Binance", 2);
        var assetDir = AssetDirectoryName.From(asset);

        WriteFeedsJson("Binance", assetDir, new
        {
            Feeds = new Dictionary<string, object>
            {
                ["funding_rate"] = new
                {
                    Interval = "8h",
                    Columns = new[] { "rate" },
                    AutoApply = new { Type = "FundingRate", RateColumn = "rate", SignConvention = (string?)null }
                }
            }
        });

        WriteFeedCsv("Binance", assetDir, "funding_rate", 2024, 1, "8h",
            "ts,rate",
            [$"{Ts(2024,1,1)},0.0001"]);

        var result = _builder.Build(_testDataRoot, asset, new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31));

        Assert.NotNull(result);
        var schema = result!.GetSchema("funding_rate");
        Assert.NotNull(schema.AutoApply);
        Assert.Equal(AutoApplyType.FundingRate, schema.AutoApply!.Type);
        Assert.Equal("rate", schema.AutoApply.RateColumn);
    }

    [Fact]
    public void Build_MultipleFeeds_RegistersAll()
    {
        var asset = CryptoPerpetualAsset.Create("BTCUSDT", "Binance", 2);
        var assetDir = AssetDirectoryName.From(asset);

        WriteFeedsJson("Binance", assetDir, new
        {
            Feeds = new Dictionary<string, object>
            {
                ["funding_rate"] = new { Interval = "8h", Columns = new[] { "rate" } },
                ["open_interest"] = new { Interval = "1h", Columns = new[] { "oi_usd", "oi_contracts" } }
            }
        });

        WriteFeedCsv("Binance", assetDir, "funding_rate", 2024, 1, "8h",
            "ts,rate", [$"{Ts(2024,1,1)},0.0001"]);

        WriteFeedCsv("Binance", assetDir, "open_interest", 2024, 1, "1h",
            "ts,oi_usd,oi_contracts", [$"{Ts(2024,1,1)},1000000.0,500.0"]);

        var result = _builder.Build(_testDataRoot, asset, new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31));

        Assert.NotNull(result);
        var schemaFunding = result!.GetSchema("funding_rate");
        var schemaOi = result.GetSchema("open_interest");
        Assert.Equal("rate", schemaFunding.ColumnNames[0]);
        Assert.Equal(2, schemaOi.ColumnNames.Length);
    }

    [Fact]
    public void Build_CryptoAsset_UsesAssetNameDirectly()
    {
        var asset = CryptoAsset.Create("ETHUSDT", "Binance", 2);
        var assetDir = AssetDirectoryName.From(asset); // ETHUSDT (no _fut suffix)
        Assert.Equal("ETHUSDT", assetDir);

        WriteFeedsJson("Binance", assetDir, new
        {
            Feeds = new Dictionary<string, object>
            {
                ["volume"] = new { Interval = "1h", Columns = new[] { "vol" } }
            }
        });

        WriteFeedCsv("Binance", assetDir, "volume", 2024, 1, "1h",
            "ts,vol", [$"{Ts(2024,1,1)},99999.0"]);

        var result = _builder.Build(_testDataRoot, asset, new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31));
        Assert.NotNull(result);
        var schema = result!.GetSchema("volume");
        Assert.Equal("vol", schema.ColumnNames[0]);
    }

    [Fact]
    public void Build_MalformedFeedsJson_ReturnsNull()
    {
        var asset = CryptoPerpetualAsset.Create("BTCUSDT", "Binance", 2);
        var assetDir = AssetDirectoryName.From(asset);

        // Write invalid JSON to feeds.json
        var dir = Path.Combine(_testDataRoot, "Binance", assetDir);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "feeds.json"), "{ invalid json !!!");

        var result = _builder.Build(_testDataRoot, asset, new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31));
        Assert.Null(result);
    }

    [Fact]
    public void Build_InvalidAutoApplyType_RegistersFeedWithNullAutoApply()
    {
        var asset = CryptoPerpetualAsset.Create("BTCUSDT", "Binance", 2);
        var assetDir = AssetDirectoryName.From(asset);

        WriteFeedsJson("Binance", assetDir, new
        {
            Feeds = new Dictionary<string, object>
            {
                ["funding_rate"] = new
                {
                    Interval = "8h",
                    Columns = new[] { "rate" },
                    AutoApply = new { Type = "InvalidType", RateColumn = "rate", SignConvention = (string?)null }
                }
            }
        });

        WriteFeedCsv("Binance", assetDir, "funding_rate", 2024, 1, "8h",
            "ts,rate", [$"{Ts(2024,1,1)},0.0001"]);

        var result = _builder.Build(_testDataRoot, asset, new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31));

        Assert.NotNull(result);
        var schema = result!.GetSchema("funding_rate");
        Assert.Equal("funding_rate", schema.FeedKey);
        Assert.Null(schema.AutoApply);
    }
}
