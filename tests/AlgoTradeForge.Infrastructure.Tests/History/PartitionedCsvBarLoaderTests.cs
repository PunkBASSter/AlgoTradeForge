using AlgoTradeForge.Domain.History;
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

    private void WriteCandlesCsv(string exchange, string symbol, int year, int month, string interval, string[] rows)
    {
        var dir = Path.Combine(_testDataRoot, exchange, symbol, "candles");
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, $"{year}-{month:D2}_{interval}.csv");
        var lines = new List<string> { "ts,o,h,l,c,vol" };
        lines.AddRange(rows);
        File.WriteAllLines(filePath, lines);
    }

    private void WriteAggregatedCsv(string exchange, string symbol, string feedId, string fileName, string[] rows)
    {
        var dir = Path.Combine(_testDataRoot, exchange, symbol, "aggregated", feedId);
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, fileName);
        var lines = new List<string> { "ts,o,h,l,c,vol" };
        lines.AddRange(rows);
        File.WriteAllLines(filePath, lines);
    }

    private DataFeedDescriptor TimeBarDescriptor(string exchange, string symbol, string feedId) =>
        new(_testDataRoot, exchange, symbol, feedId, DataFeedKind.TimeBar);

    private DataFeedDescriptor AltBarDescriptor(string exchange, string symbol, string feedId) =>
        new(_testDataRoot, exchange, symbol, feedId, DataFeedKind.AltBar);

    private static long Ts(int year, int month, int day, int hour = 0, int min = 0) =>
        new DateTimeOffset(year, month, day, hour, min, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    // -------------------------------------------------------------------------
    // TimeBar happy path
    // -------------------------------------------------------------------------

    [Fact]
    public void Load_SingleMonth_ReturnsCorrectBars()
    {
        WriteCandlesCsv("Binance", "BTCUSDT", 2024, 1, "1m",
        [
            $"{Ts(2024,1,1)},6743215,6745100,6741000,6744300,153240",
            $"{Ts(2024,1,1,0,1)},6743300,6745200,6741100,6744400,153300"
        ]);

        var series = _loader.Load(
            TimeBarDescriptor("Binance", "BTCUSDT", "1m"),
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31));

        Assert.Equal(2, series.Count);
        Assert.Equal(6743215L, series[0].Open);
        Assert.Equal(6745100L, series[0].High);
        Assert.Equal(6741000L, series[0].Low);
        Assert.Equal(6744300L, series[0].Close);
        Assert.Equal(153240L,  series[0].Volume);
        Assert.Equal(Ts(2024, 1, 1), series[0].TimestampMs);
    }

    [Fact]
    public void Load_MultiMonth_ReturnsAllBars()
    {
        WriteCandlesCsv("Binance", "BTCUSDT", 2024, 1, "1m",
            [$"{Ts(2024,1,15)},100,200,50,150,1000"]);
        WriteCandlesCsv("Binance", "BTCUSDT", 2024, 2, "1m",
            [$"{Ts(2024,2,10)},110,210,60,160,1100"]);

        var series = _loader.Load(
            TimeBarDescriptor("Binance", "BTCUSDT", "1m"),
            new DateOnly(2024, 1, 1), new DateOnly(2024, 2, 28));

        Assert.Equal(2, series.Count);
        Assert.Equal(100L, series[0].Open);
        Assert.Equal(110L, series[1].Open);
    }

    [Fact]
    public void Load_MultiMonth_SpanningYearBoundary()
    {
        WriteCandlesCsv("Binance", "BTCUSDT", 2024, 12, "1m",
            [$"{Ts(2024,12,31,23,59)},100,200,50,150,1000"]);
        WriteCandlesCsv("Binance", "BTCUSDT", 2025, 1, "1m",
            [$"{Ts(2025,1,1)},110,210,60,160,1100"]);

        var series = _loader.Load(
            TimeBarDescriptor("Binance", "BTCUSDT", "1m"),
            new DateOnly(2024, 12, 1), new DateOnly(2025, 1, 31));

        Assert.Equal(2, series.Count);
    }

    [Fact]
    public void Load_FiltersRowsOutsideDateRange()
    {
        WriteCandlesCsv("Binance", "BTCUSDT", 2024, 1, "1m",
        [
            $"{Ts(2024,1,1)},100,200,50,150,1000",
            $"{Ts(2024,1,15)},110,210,60,160,1100",
            $"{Ts(2024,1,31)},120,220,70,170,1200"
        ]);

        var series = _loader.Load(
            TimeBarDescriptor("Binance", "BTCUSDT", "1m"),
            new DateOnly(2024, 1, 10), new DateOnly(2024, 1, 20));

        var bar = Assert.Single(series);
        Assert.Equal(110L, bar.Open);
    }

    [Fact]
    public void Load_IntervalInFilename_1h()
    {
        WriteCandlesCsv("Binance", "BTCUSDT", 2024, 1, "1h",
            [$"{Ts(2024,1,1)},1000,1100,900,1050,5000"]);

        var series = _loader.Load(
            TimeBarDescriptor("Binance", "BTCUSDT", "1h"),
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31));

        var bar = Assert.Single(series);
        Assert.Equal(1000L, bar.Open);
    }

    [Fact]
    public void Load_MissingMonthFile_SkipsGracefully()
    {
        WriteCandlesCsv("Binance", "BTCUSDT", 2024, 1, "1m",
            [$"{Ts(2024,1,1)},100,200,50,150,1000"]);
        // No Feb file

        var series = _loader.Load(
            TimeBarDescriptor("Binance", "BTCUSDT", "1m"),
            new DateOnly(2024, 1, 1), new DateOnly(2024, 2, 28));

        Assert.Single(series);
    }

    [Fact]
    public void Load_NoDataInRange_ReturnsEmptySeries()
    {
        var series = _loader.Load(
            TimeBarDescriptor("Binance", "BTCUSDT", "1m"),
            new DateOnly(2024, 6, 1), new DateOnly(2024, 6, 30));

        Assert.Empty(series);
    }

    // -------------------------------------------------------------------------
    // P1a-29, P1a-30 — per-FeedId glob filter for mixed-timeframe candles/
    // -------------------------------------------------------------------------

    [Fact]
    public void Load_TimeBar_PerFeedIdFilter_ExcludesOtherIntervals()
    {
        // Plant 1m, 5m, AND a 1h file under candles/ — loading "1m" must pick up ONLY 1m.
        WriteCandlesCsv("Binance", "BTCUSDT", 2024, 1, "1m",
            [$"{Ts(2024,1,1)},1,1,1,1,100"]);
        WriteCandlesCsv("Binance", "BTCUSDT", 2024, 1, "5m",
            [$"{Ts(2024,1,1)},5,5,5,5,500"]);
        WriteCandlesCsv("Binance", "BTCUSDT", 2024, 1, "1h",
            [$"{Ts(2024,1,1)},60,60,60,60,6000"]);

        var series = _loader.Load(
            TimeBarDescriptor("Binance", "BTCUSDT", "1m"),
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31));

        var bar = Assert.Single(series);
        Assert.Equal(1L, bar.Open);
        Assert.Equal(100L, bar.Volume);
    }

    [Fact]
    public void Load_TimeBar_5mFeedId_DoesNotPickUp1m()
    {
        WriteCandlesCsv("Binance", "BTCUSDT", 2024, 1, "1m",
            [$"{Ts(2024,1,1)},1,1,1,1,100"]);
        WriteCandlesCsv("Binance", "BTCUSDT", 2024, 1, "5m",
            [$"{Ts(2024,1,1)},5,5,5,5,500"]);

        var series = _loader.Load(
            TimeBarDescriptor("Binance", "BTCUSDT", "5m"),
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31));

        var bar = Assert.Single(series);
        Assert.Equal(5L, bar.Open);
    }

    // -------------------------------------------------------------------------
    // GetLastTimestamp
    // -------------------------------------------------------------------------

    [Fact]
    public void GetLastTimestamp_NoDirectory_ReturnsNull()
    {
        var result = _loader.GetLastTimestamp(
            TimeBarDescriptor("Binance", "MISSING", "1m"));
        Assert.Null(result);
    }

    [Fact]
    public void GetLastTimestamp_WithData_ReturnsLastTimestamp()
    {
        var ts1 = Ts(2024, 3, 1);
        var ts2 = Ts(2024, 3, 15, 12, 30);
        WriteCandlesCsv("Binance", "BTCUSDT", 2024, 3, "1m",
        [
            $"{ts1},100,200,50,150,1000",
            $"{ts2},110,210,60,160,1100"
        ]);

        var result = _loader.GetLastTimestamp(
            TimeBarDescriptor("Binance", "BTCUSDT", "1m"));
        Assert.NotNull(result);
        Assert.Equal(ts2, result!.Value.ToUnixTimeMilliseconds());
    }

    [Fact]
    public void GetLastTimestamp_MultipleFiles_ReturnsLatest()
    {
        var tsJan = Ts(2024, 1, 31);
        var tsFeb = Ts(2024, 2, 29);
        WriteCandlesCsv("Binance", "BTCUSDT", 2024, 1, "1m", [$"{tsJan},100,200,50,150,1000"]);
        WriteCandlesCsv("Binance", "BTCUSDT", 2024, 2, "1m", [$"{tsFeb},110,210,60,160,1100"]);

        var result = _loader.GetLastTimestamp(
            TimeBarDescriptor("Binance", "BTCUSDT", "1m"));
        Assert.NotNull(result);
        Assert.Equal(tsFeb, result!.Value.ToUnixTimeMilliseconds());
    }

    // -------------------------------------------------------------------------
    // Malformed row handling
    // -------------------------------------------------------------------------

    [Fact]
    public void Load_EmptyLines_SkipsGracefully()
    {
        WriteCandlesCsv("Binance", "BTCUSDT", 2024, 1, "1m",
        [
            "",
            $"{Ts(2024,1,1)},100,200,50,150,1000",
            "",
            $"{Ts(2024,1,1,0,1)},110,210,60,160,1100",
            ""
        ]);

        var series = _loader.Load(
            TimeBarDescriptor("Binance", "BTCUSDT", "1m"),
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31));

        Assert.Equal(2, series.Count);
    }

    [Fact]
    public void Load_FewerThanSixColumns_SkipsRow()
    {
        WriteCandlesCsv("Binance", "BTCUSDT", 2024, 1, "1m",
        [
            $"{Ts(2024,1,1)},100,200,50,150,1000",
            $"{Ts(2024,1,1,0,1)},110,210",
            $"{Ts(2024,1,1,0,2)},120,220,70,170,1200"
        ]);

        var series = _loader.Load(
            TimeBarDescriptor("Binance", "BTCUSDT", "1m"),
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31));

        Assert.Equal(2, series.Count);
        Assert.Equal(100L, series[0].Open);
        Assert.Equal(120L, series[1].Open);
    }

    [Fact]
    public void Load_NonNumericValues_SkipsRow()
    {
        WriteCandlesCsv("Binance", "BTCUSDT", 2024, 1, "1m",
        [
            $"{Ts(2024,1,1)},100,200,50,150,1000",
            $"{Ts(2024,1,1,0,1)},abc,210,60,160,1100",
            $"{Ts(2024,1,1,0,2)},120,220,70,170,1200"
        ]);

        var series = _loader.Load(
            TimeBarDescriptor("Binance", "BTCUSDT", "1m"),
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31));

        Assert.Equal(2, series.Count);
        Assert.Equal(100L, series[0].Open);
        Assert.Equal(120L, series[1].Open);
    }

    [Fact]
    public void Load_NonNumericTimestamp_SkipsRow()
    {
        WriteCandlesCsv("Binance", "BTCUSDT", 2024, 1, "1m",
        [
            $"{Ts(2024,1,1)},100,200,50,150,1000",
            "not-a-timestamp,110,210,60,160,1100",
            $"{Ts(2024,1,1,0,2)},120,220,70,170,1200"
        ]);

        var series = _loader.Load(
            TimeBarDescriptor("Binance", "BTCUSDT", "1m"),
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31));

        Assert.Equal(2, series.Count);
    }

    // -------------------------------------------------------------------------
    // P1a-26 – AltBar reads from aggregated/{feedId}/*.csv
    // -------------------------------------------------------------------------

    [Fact]
    public void Load_AltBar_LoadsFromAggregatedFeedDir()
    {
        WriteAggregatedCsv("Binance", "BTCUSDT", "EqV_1m_1000", "2026-04.csv",
            [$"{Ts(2026,4,1)},10,20,5,15,1000"]);
        WriteAggregatedCsv("Binance", "BTCUSDT", "EqV_1m_1000", "2026-05.csv",
            [$"{Ts(2026,5,1)},20,30,15,25,2000"]);

        var series = _loader.Load(
            AltBarDescriptor("Binance", "BTCUSDT", "EqV_1m_1000"),
            new DateOnly(2026, 4, 1), new DateOnly(2026, 5, 31));

        Assert.Equal(2, series.Count);
        Assert.Equal(10L, series[0].Open);
        Assert.Equal(20L, series[1].Open);
    }

    [Fact]
    public void Load_AltBar_PartNumberedPartitionsLexSortChronological()
    {
        // 2026-04.csv  < 2026-05.p01.csv < 2026-05.p02.csv  (lex order = chronological)
        WriteAggregatedCsv("Binance", "BTCUSDT", "EqV_1m_1000", "2026-04.csv",
            [$"{Ts(2026,4,15)},10,20,5,15,1000"]);
        WriteAggregatedCsv("Binance", "BTCUSDT", "EqV_1m_1000", "2026-05.p01.csv",
            [$"{Ts(2026,5,5)},20,30,15,25,2000"]);
        WriteAggregatedCsv("Binance", "BTCUSDT", "EqV_1m_1000", "2026-05.p02.csv",
            [$"{Ts(2026,5,20)},30,40,25,35,3000"]);

        var series = _loader.Load(
            AltBarDescriptor("Binance", "BTCUSDT", "EqV_1m_1000"),
            new DateOnly(2026, 4, 1), new DateOnly(2026, 5, 31));

        Assert.Equal(3, series.Count);
        Assert.Equal(10L, series[0].Open);
        Assert.Equal(20L, series[1].Open);
        Assert.Equal(30L, series[2].Open);
    }
}
