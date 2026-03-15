using AlgoTradeForge.Infrastructure.History;
using Xunit;

namespace AlgoTradeForge.Infrastructure.Tests.History;

public class CsvFeedSeriesLoaderTests : IDisposable
{
    private readonly string _testDataRoot;
    private readonly CsvFeedSeriesLoader _loader = new();

    public CsvFeedSeriesLoaderTests()
    {
        _testDataRoot = Path.Combine(Path.GetTempPath(), $"FeedLoader_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDataRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDataRoot))
            Directory.Delete(_testDataRoot, recursive: true);
    }

    private void WriteCsv(string exchange, string assetDir, string feedName, int year, int month,
        string? interval, string header, string[] rows)
    {
        var dir = Path.Combine(_testDataRoot, exchange, assetDir, feedName);
        Directory.CreateDirectory(dir);
        var fileName = string.IsNullOrEmpty(interval)
            ? $"{year}-{month:D2}.csv"
            : $"{year}-{month:D2}_{interval}.csv";
        var lines = new List<string> { header };
        lines.AddRange(rows);
        File.WriteAllLines(Path.Combine(dir, fileName), lines);
    }

    private static long Ts(int year, int month, int day, int hour = 0) =>
        new DateTimeOffset(year, month, day, hour, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    [Fact]
    public void Load_SingleMonth_ReturnsCorrectFeedSeries()
    {
        var ts1 = Ts(2024, 1, 1, 0);
        var ts2 = Ts(2024, 1, 1, 8);
        WriteCsv("Binance", "BTCUSDT_fut", "funding_rate", 2024, 1, "8h",
            "ts,rate",
            [
                $"{ts1},0.0001",
                $"{ts2},0.00015"
            ]);

        var result = _loader.Load(
            _testDataRoot, "Binance", "BTCUSDT_fut", "funding_rate", "8h",
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31));

        Assert.NotNull(result);
        Assert.Equal(2, result!.Count);
        Assert.Equal(ts1, result.Timestamps[0]);
        Assert.Equal(ts2, result.Timestamps[1]);
        Assert.Single(result.Columns);
        Assert.Equal(0.0001, result.Columns[0][0], precision: 8);
        Assert.Equal(0.00015, result.Columns[0][1], precision: 8);
    }

    [Fact]
    public void Load_MultipleColumns_RoundTrips()
    {
        var ts1 = Ts(2024, 1, 1);
        WriteCsv("Binance", "BTCUSDT", "oi", 2024, 1, null,
            "ts,oi_usd,oi_contracts",
            [
                $"{ts1},1000000.5,500.25"
            ]);

        var result = _loader.Load(
            _testDataRoot, "Binance", "BTCUSDT", "oi", "",
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31));

        Assert.NotNull(result);
        Assert.Equal(2, result!.ColumnCount);
        Assert.Equal(1000000.5, result.Columns[0][0], precision: 4);
        Assert.Equal(500.25, result.Columns[1][0], precision: 4);
    }

    [Fact]
    public void Load_MultiMonth_CombinesData()
    {
        WriteCsv("Binance", "BTCUSDT_fut", "funding_rate", 2024, 1, "8h",
            "ts,rate",
            [$"{Ts(2024,1,15)},0.0001"]);
        WriteCsv("Binance", "BTCUSDT_fut", "funding_rate", 2024, 2, "8h",
            "ts,rate",
            [$"{Ts(2024,2,15)},0.0002"]);

        var result = _loader.Load(
            _testDataRoot, "Binance", "BTCUSDT_fut", "funding_rate", "8h",
            new DateOnly(2024, 1, 1), new DateOnly(2024, 2, 28));

        Assert.NotNull(result);
        Assert.Equal(2, result!.Count);
    }

    [Fact]
    public void Load_NoFiles_ReturnsNull()
    {
        var result = _loader.Load(
            _testDataRoot, "Binance", "BTCUSDT_fut", "funding_rate", "8h",
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31));

        Assert.Null(result);
    }

    [Fact]
    public void Load_FiltersByDateRange()
    {
        var tsEarly = Ts(2024, 1, 1);
        var tsMid = Ts(2024, 1, 15);
        var tsLate = Ts(2024, 1, 31);
        WriteCsv("Binance", "BTCUSDT_fut", "funding_rate", 2024, 1, "8h",
            "ts,rate",
            [
                $"{tsEarly},0.0001",
                $"{tsMid},0.0002",
                $"{tsLate},0.0003"
            ]);

        var result = _loader.Load(
            _testDataRoot, "Binance", "BTCUSDT_fut", "funding_rate", "8h",
            new DateOnly(2024, 1, 10), new DateOnly(2024, 1, 20));

        Assert.NotNull(result);
        Assert.Single(result!.Timestamps);
        Assert.Equal(tsMid, result.Timestamps[0]);
        Assert.Equal(0.0002, result.Columns[0][0], precision: 8);
    }

    [Fact]
    public void Load_InvariantCulture_ParsesDecimalCorrectly()
    {
        var ts1 = Ts(2024, 1, 1);
        WriteCsv("Binance", "BTCUSDT_fut", "funding_rate", 2024, 1, null,
            "ts,rate",
            [
                $"{ts1},1.23456789"
            ]);

        var result = _loader.Load(
            _testDataRoot, "Binance", "BTCUSDT_fut", "funding_rate", "",
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31));

        Assert.NotNull(result);
        Assert.Equal(1.23456789, result!.Columns[0][0], precision: 8);
    }

    // -------------------------------------------------------------------------
    // Malformed data handling (T5)
    // -------------------------------------------------------------------------

    [Fact]
    public void Load_EmptyLines_SkipsGracefully()
    {
        var ts1 = Ts(2024, 1, 1);
        var ts2 = Ts(2024, 1, 1, 8);
        WriteCsv("Binance", "BTCUSDT_fut", "funding_rate", 2024, 1, "8h",
            "ts,rate",
            [
                "",
                $"{ts1},0.0001",
                "",
                $"{ts2},0.00015",
                ""
            ]);

        var result = _loader.Load(
            _testDataRoot, "Binance", "BTCUSDT_fut", "funding_rate", "8h",
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31));

        Assert.NotNull(result);
        Assert.Equal(2, result!.Count);
    }

    [Fact]
    public void Load_FewerColumnsThanHeader_FillsZero()
    {
        var ts1 = Ts(2024, 1, 1);
        var ts2 = Ts(2024, 1, 1, 8);
        WriteCsv("Binance", "BTCUSDT_fut", "oi", 2024, 1, null,
            "ts,oi_usd,oi_contracts",
            [
                $"{ts1},1000000.0,500.0",
                $"{ts2},2000000.0"
            ]);

        var result = _loader.Load(
            _testDataRoot, "Binance", "BTCUSDT_fut", "oi", "",
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31));

        Assert.NotNull(result);
        Assert.Equal(2, result!.Count);
        // Second row's missing column should be 0
        Assert.Equal(0d, result.Columns[1][1]);
    }

    [Fact]
    public void Load_NonNumericValue_SkipsRow()
    {
        var ts1 = Ts(2024, 1, 1);
        var ts2 = Ts(2024, 1, 1, 8);
        WriteCsv("Binance", "BTCUSDT_fut", "funding_rate", 2024, 1, "8h",
            "ts,rate",
            [
                $"{ts1},0.0001",
                $"{ts2},not-a-number"
            ]);

        var result = _loader.Load(
            _testDataRoot, "Binance", "BTCUSDT_fut", "funding_rate", "8h",
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31));

        Assert.NotNull(result);
        Assert.Single(result!.Timestamps);
        Assert.Equal(ts1, result.Timestamps[0]);
    }

    [Fact]
    public void Load_NonNumericTimestamp_SkipsRow()
    {
        var ts1 = Ts(2024, 1, 1);
        WriteCsv("Binance", "BTCUSDT_fut", "funding_rate", 2024, 1, "8h",
            "ts,rate",
            [
                $"{ts1},0.0001",
                "invalid-ts,0.00015"
            ]);

        var result = _loader.Load(
            _testDataRoot, "Binance", "BTCUSDT_fut", "funding_rate", "8h",
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31));

        Assert.NotNull(result);
        Assert.Single(result!.Timestamps);
    }

    [Fact]
    public void Load_SingleColumnRow_SkipsRow()
    {
        var ts1 = Ts(2024, 1, 1);
        WriteCsv("Binance", "BTCUSDT_fut", "funding_rate", 2024, 1, "8h",
            "ts,rate",
            [
                $"{ts1},0.0001",
                "just-one-field"
            ]);

        var result = _loader.Load(
            _testDataRoot, "Binance", "BTCUSDT_fut", "funding_rate", "8h",
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31));

        Assert.NotNull(result);
        Assert.Single(result!.Timestamps);
    }
}
