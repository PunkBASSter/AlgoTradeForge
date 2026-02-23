using AlgoTradeForge.Infrastructure.CandleIngestion;
using AlgoTradeForge.Infrastructure.IO;
using Xunit;

namespace AlgoTradeForge.Infrastructure.Tests.CandleIngestion;

public class CsvInt64BarLoaderTests : IDisposable
{
    private readonly string _testDataRoot;
    private readonly FileStorage _fs = new();
    private readonly CsvInt64BarLoader _loader;

    public CsvInt64BarLoaderTests()
    {
        _testDataRoot = Path.Combine(Path.GetTempPath(), $"CsvLoaderTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDataRoot);
        _loader = new CsvInt64BarLoader(_fs);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDataRoot))
            Directory.Delete(_testDataRoot, recursive: true);
    }

    private void WriteCsv(string exchange, string symbol, int year, int month, string[] rows)
    {
        var dir = Path.Combine(_testDataRoot, exchange, symbol, year.ToString());
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, $"{year}-{month:D2}.csv");
        var lines = new List<string> { "Timestamp,Open,High,Low,Close,Volume" };
        lines.AddRange(rows);
        _fs.WriteAllLines(filePath, lines);
    }

    [Fact]
    public void Load_SingleMonth_ReturnsCorrectBars()
    {
        WriteCsv("Binance", "BTCUSDT", 2024, 1,
        [
            "2024-01-01T00:00:00+00:00,6743215,6745100,6741000,6744300,153240",
            "2024-01-01T00:01:00+00:00,6743300,6745200,6741100,6744400,153300"
        ]);

        var series = _loader.Load(
            _testDataRoot, "Binance", "BTCUSDT", 2,
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31),
            TimeSpan.FromMinutes(1));

        Assert.Equal(2, series.Count);
        Assert.Equal(6743215L, series[0].Open);
        Assert.Equal(6745100L, series[0].High);
        Assert.Equal(6741000L, series[0].Low);
        Assert.Equal(6744300L, series[0].Close);
        Assert.Equal(153240L, series[0].Volume);
    }

    [Fact]
    public void Load_MultiMonth_SpanningYearBoundary()
    {
        WriteCsv("Binance", "BTCUSDT", 2024, 12,
        [
            "2024-12-31T23:59:00+00:00,100,200,50,150,1000"
        ]);
        WriteCsv("Binance", "BTCUSDT", 2025, 1,
        [
            "2025-01-01T00:00:00+00:00,110,210,60,160,1100"
        ]);

        var series = _loader.Load(
            _testDataRoot, "Binance", "BTCUSDT", 2,
            new DateOnly(2024, 12, 1), new DateOnly(2025, 1, 31),
            TimeSpan.FromMinutes(1));

        Assert.Equal(2, series.Count);
    }

    [Fact]
    public void Load_FiltersRowsOutsideDateRange()
    {
        WriteCsv("Binance", "BTCUSDT", 2024, 1,
        [
            "2024-01-01T00:00:00+00:00,100,200,50,150,1000",
            "2024-01-15T00:00:00+00:00,110,210,60,160,1100",
            "2024-01-31T00:00:00+00:00,120,220,70,170,1200"
        ]);

        var series = _loader.Load(
            _testDataRoot, "Binance", "BTCUSDT", 2,
            new DateOnly(2024, 1, 10), new DateOnly(2024, 1, 20),
            TimeSpan.FromMinutes(1));

        var bar = Assert.Single(series);
        Assert.Equal(110L, bar.Open);
    }

    [Fact]
    public void Load_MissingMonthFile_SkipsGracefully()
    {
        WriteCsv("Binance", "BTCUSDT", 2024, 1,
        [
            "2024-01-01T00:00:00+00:00,100,200,50,150,1000"
        ]);

        var series = _loader.Load(
            _testDataRoot, "Binance", "BTCUSDT", 2,
            new DateOnly(2024, 1, 1), new DateOnly(2024, 2, 28),
            TimeSpan.FromMinutes(1));

        Assert.Single(series);
    }

    [Fact]
    public void Load_NoDataInRange_ReturnsEmptySeries()
    {
        var series = _loader.Load(
            _testDataRoot, "Binance", "BTCUSDT", 2,
            new DateOnly(2024, 6, 1), new DateOnly(2024, 6, 30),
            TimeSpan.FromMinutes(1));

        Assert.Empty(series);
    }

    [Fact]
    public void Load_UsesLongParsing_NotFloat()
    {
        WriteCsv("Binance", "BTCUSDT", 2024, 1,
        [
            "2024-01-01T00:00:00+00:00,9007199254740993,9007199254740994,9007199254740992,9007199254740993,100"
        ]);

        var series = _loader.Load(
            _testDataRoot, "Binance", "BTCUSDT", 2,
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31),
            TimeSpan.FromMinutes(1));

        Assert.Equal(9007199254740993L, series[0].Open);
    }

    [Fact]
    public void GetLastTimestamp_NoData_ReturnsNull()
    {
        var result = _loader.GetLastTimestamp(_testDataRoot, "Binance", "BTCUSDT");
        Assert.Null(result);
    }

    [Fact]
    public void GetLastTimestamp_WithData_ReturnsLastTimestamp()
    {
        WriteCsv("Binance", "BTCUSDT", 2024, 3,
        [
            "2024-03-01T00:00:00+00:00,100,200,50,150,1000",
            "2024-03-15T12:30:00+00:00,110,210,60,160,1100"
        ]);

        var result = _loader.GetLastTimestamp(_testDataRoot, "Binance", "BTCUSDT");
        Assert.NotNull(result);
        Assert.Equal(new DateTimeOffset(2024, 3, 15, 12, 30, 0, TimeSpan.Zero), result.Value);
    }
}
