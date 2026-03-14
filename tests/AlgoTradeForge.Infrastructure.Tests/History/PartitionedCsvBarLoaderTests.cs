using AlgoTradeForge.Infrastructure.History;
using Xunit;

namespace AlgoTradeForge.Infrastructure.Tests.History;

public class PartitionedCsvBarLoaderTests : IDisposable
{
    private readonly string _testDataRoot;
    private readonly PartitionedCsvBarLoader _loader = new();

    public PartitionedCsvBarLoaderTests()
    {
        _testDataRoot = Path.Combine(Path.GetTempPath(), $"PartitionedBarLoader_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDataRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDataRoot))
            Directory.Delete(_testDataRoot, recursive: true);
    }

    private void WriteCsv(string exchange, string symbol, int year, int month, string interval, string[] rows)
    {
        var dir = Path.Combine(_testDataRoot, exchange, symbol, "candles");
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, $"{year}-{month:D2}_{interval}.csv");
        var lines = new List<string> { "ts,o,h,l,c,vol" };
        lines.AddRange(rows);
        File.WriteAllLines(filePath, lines);
    }

    // ts for 2024-01-01 00:00:00 UTC
    private static long Ts(int year, int month, int day, int hour = 0, int min = 0) =>
        new DateTimeOffset(year, month, day, hour, min, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    [Fact]
    public void Load_SingleMonth_ReturnsCorrectBars()
    {
        WriteCsv("Binance", "BTCUSDT", 2024, 1, "1m",
        [
            $"{Ts(2024,1,1)},6743215,6745100,6741000,6744300,153240",
            $"{Ts(2024,1,1,0,1)},6743300,6745200,6741100,6744400,153300"
        ]);

        var series = _loader.Load(
            _testDataRoot, "Binance", "BTCUSDT",
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31),
            TimeSpan.FromMinutes(1));

        Assert.Equal(2, series.Count);
        Assert.Equal(6743215L, series[0].Open);
        Assert.Equal(6745100L, series[0].High);
        Assert.Equal(6741000L, series[0].Low);
        Assert.Equal(6744300L, series[0].Close);
        Assert.Equal(153240L, series[0].Volume);
        Assert.Equal(Ts(2024, 1, 1), series[0].TimestampMs);
    }

    [Fact]
    public void Load_MultiMonth_ReturnsAllBars()
    {
        WriteCsv("Binance", "BTCUSDT", 2024, 1, "1m",
        [
            $"{Ts(2024,1,15)},100,200,50,150,1000"
        ]);
        WriteCsv("Binance", "BTCUSDT", 2024, 2, "1m",
        [
            $"{Ts(2024,2,10)},110,210,60,160,1100"
        ]);

        var series = _loader.Load(
            _testDataRoot, "Binance", "BTCUSDT",
            new DateOnly(2024, 1, 1), new DateOnly(2024, 2, 28),
            TimeSpan.FromMinutes(1));

        Assert.Equal(2, series.Count);
        Assert.Equal(100L, series[0].Open);
        Assert.Equal(110L, series[1].Open);
    }

    [Fact]
    public void Load_MultiMonth_SpanningYearBoundary()
    {
        WriteCsv("Binance", "BTCUSDT", 2024, 12, "1m",
        [
            $"{Ts(2024,12,31,23,59)},100,200,50,150,1000"
        ]);
        WriteCsv("Binance", "BTCUSDT", 2025, 1, "1m",
        [
            $"{Ts(2025,1,1)},110,210,60,160,1100"
        ]);

        var series = _loader.Load(
            _testDataRoot, "Binance", "BTCUSDT",
            new DateOnly(2024, 12, 1), new DateOnly(2025, 1, 31),
            TimeSpan.FromMinutes(1));

        Assert.Equal(2, series.Count);
    }

    [Fact]
    public void Load_FiltersRowsOutsideDateRange()
    {
        WriteCsv("Binance", "BTCUSDT", 2024, 1, "1m",
        [
            $"{Ts(2024,1,1)},100,200,50,150,1000",
            $"{Ts(2024,1,15)},110,210,60,160,1100",
            $"{Ts(2024,1,31)},120,220,70,170,1200"
        ]);

        var series = _loader.Load(
            _testDataRoot, "Binance", "BTCUSDT",
            new DateOnly(2024, 1, 10), new DateOnly(2024, 1, 20),
            TimeSpan.FromMinutes(1));

        var bar = Assert.Single(series);
        Assert.Equal(110L, bar.Open);
    }

    [Fact]
    public void Load_IntervalInFilename_1h()
    {
        WriteCsv("Binance", "BTCUSDT", 2024, 1, "1h",
        [
            $"{Ts(2024,1,1)},1000,1100,900,1050,5000"
        ]);

        var series = _loader.Load(
            _testDataRoot, "Binance", "BTCUSDT",
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31),
            TimeSpan.FromHours(1));

        var bar = Assert.Single(series);
        Assert.Equal(1000L, bar.Open);
    }

    [Fact]
    public void Load_MissingMonthFile_SkipsGracefully()
    {
        WriteCsv("Binance", "BTCUSDT", 2024, 1, "1m",
        [
            $"{Ts(2024,1,1)},100,200,50,150,1000"
        ]);
        // No Feb file

        var series = _loader.Load(
            _testDataRoot, "Binance", "BTCUSDT",
            new DateOnly(2024, 1, 1), new DateOnly(2024, 2, 28),
            TimeSpan.FromMinutes(1));

        Assert.Single(series);
    }

    [Fact]
    public void Load_NoDataInRange_ReturnsEmptySeries()
    {
        var series = _loader.Load(
            _testDataRoot, "Binance", "BTCUSDT",
            new DateOnly(2024, 6, 1), new DateOnly(2024, 6, 30),
            TimeSpan.FromMinutes(1));

        Assert.Empty(series);
    }

    [Fact]
    public void GetLastTimestamp_NoDirectory_ReturnsNull()
    {
        var result = _loader.GetLastTimestamp(_testDataRoot, "Binance", "MISSING");
        Assert.Null(result);
    }

    [Fact]
    public void GetLastTimestamp_WithData_ReturnsLastTimestamp()
    {
        var ts1 = Ts(2024, 3, 1);
        var ts2 = Ts(2024, 3, 15, 12, 30);
        WriteCsv("Binance", "BTCUSDT", 2024, 3, "1m",
        [
            $"{ts1},100,200,50,150,1000",
            $"{ts2},110,210,60,160,1100"
        ]);

        var result = _loader.GetLastTimestamp(_testDataRoot, "Binance", "BTCUSDT");
        Assert.NotNull(result);
        Assert.Equal(ts2, result!.Value.ToUnixTimeMilliseconds());
    }

    [Fact]
    public void GetLastTimestamp_MultipleFiles_ReturnsLatest()
    {
        var tsJan = Ts(2024, 1, 31);
        var tsFeb = Ts(2024, 2, 29);
        WriteCsv("Binance", "BTCUSDT", 2024, 1, "1m", [$"{tsJan},100,200,50,150,1000"]);
        WriteCsv("Binance", "BTCUSDT", 2024, 2, "1m", [$"{tsFeb},110,210,60,160,1100"]);

        var result = _loader.GetLastTimestamp(_testDataRoot, "Binance", "BTCUSDT");
        Assert.NotNull(result);
        Assert.Equal(tsFeb, result!.Value.ToUnixTimeMilliseconds());
    }

    [Theory]
    [InlineData(1, "1m")]
    [InlineData(5, "5m")]
    [InlineData(15, "15m")]
    [InlineData(30, "30m")]
    [InlineData(60, "1h")]
    [InlineData(240, "4h")]
    [InlineData(1440, "1d")]
    [InlineData(10080, "1w")]
    public void IntervalToString_ReturnsExpected(int minutes, string expected)
    {
        Assert.Equal(expected, PartitionedCsvBarLoader.IntervalToString(TimeSpan.FromMinutes(minutes)));
    }

    [Fact]
    public void IntervalToString_UnsupportedInterval_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => PartitionedCsvBarLoader.IntervalToString(TimeSpan.FromMinutes(7)));
    }

    // -------------------------------------------------------------------------
    // Malformed row handling (T7)
    // -------------------------------------------------------------------------

    [Fact]
    public void Load_EmptyLines_SkipsGracefully()
    {
        WriteCsv("Binance", "BTCUSDT", 2024, 1, "1m",
        [
            "",
            $"{Ts(2024,1,1)},100,200,50,150,1000",
            "",
            $"{Ts(2024,1,1,0,1)},110,210,60,160,1100",
            ""
        ]);

        var series = _loader.Load(
            _testDataRoot, "Binance", "BTCUSDT",
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31),
            TimeSpan.FromMinutes(1));

        Assert.Equal(2, series.Count);
    }

    [Fact]
    public void Load_FewerThanSixColumns_SkipsRow()
    {
        WriteCsv("Binance", "BTCUSDT", 2024, 1, "1m",
        [
            $"{Ts(2024,1,1)},100,200,50,150,1000",
            $"{Ts(2024,1,1,0,1)},110,210",
            $"{Ts(2024,1,1,0,2)},120,220,70,170,1200"
        ]);

        var series = _loader.Load(
            _testDataRoot, "Binance", "BTCUSDT",
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31),
            TimeSpan.FromMinutes(1));

        Assert.Equal(2, series.Count);
        Assert.Equal(100L, series[0].Open);
        Assert.Equal(120L, series[1].Open);
    }

    [Fact]
    public void Load_NonNumericValues_SkipsRow()
    {
        WriteCsv("Binance", "BTCUSDT", 2024, 1, "1m",
        [
            $"{Ts(2024,1,1)},100,200,50,150,1000",
            $"{Ts(2024,1,1,0,1)},abc,210,60,160,1100",
            $"{Ts(2024,1,1,0,2)},120,220,70,170,1200"
        ]);

        var series = _loader.Load(
            _testDataRoot, "Binance", "BTCUSDT",
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31),
            TimeSpan.FromMinutes(1));

        Assert.Equal(2, series.Count);
        Assert.Equal(100L, series[0].Open);
        Assert.Equal(120L, series[1].Open);
    }

    [Fact]
    public void Load_NonNumericTimestamp_SkipsRow()
    {
        WriteCsv("Binance", "BTCUSDT", 2024, 1, "1m",
        [
            $"{Ts(2024,1,1)},100,200,50,150,1000",
            "not-a-timestamp,110,210,60,160,1100",
            $"{Ts(2024,1,1,0,2)},120,220,70,170,1200"
        ]);

        var series = _loader.Load(
            _testDataRoot, "Binance", "BTCUSDT",
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31),
            TimeSpan.FromMinutes(1));

        Assert.Equal(2, series.Count);
    }
}
