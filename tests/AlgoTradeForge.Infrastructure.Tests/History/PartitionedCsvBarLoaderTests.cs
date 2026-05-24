using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Infrastructure.History;
using AlgoTradeForge.Storage;
using Xunit;

namespace AlgoTradeForge.Infrastructure.Tests.History;

public class PartitionedCsvBarLoaderTests : IDisposable
{
    private readonly string _testDataRoot;
    private readonly PartitionedCsvBarLoader _loader = new(new LocalFileStorage());
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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

    private DataFeedDescriptor TickDescriptor(string exchange, string symbol) =>
        new(_testDataRoot, exchange, symbol, "ticks", DataFeedKind.Tick);

    private DataFeedDescriptor SideDescriptor(string exchange, string symbol, string feedId) =>
        new(_testDataRoot, exchange, symbol, feedId, DataFeedKind.Side);

    private void WriteTicksCsv(string exchange, string symbol, int year, int month, int day, string[] rows)
    {
        var dir = Path.Combine(_testDataRoot, exchange, symbol, "ticks");
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, $"{year}-{month:D2}-{day:D2}.csv");
        var lines = new List<string> { "ts,o,h,l,c,vol" };
        lines.AddRange(rows);
        File.WriteAllLines(filePath, lines);
    }

    private void WriteSidecarCsv(string exchange, string symbol, string sidecarFeedId, string fileName, string[] rows)
    {
        var dir = Path.Combine(_testDataRoot, exchange, symbol, "aggregated", sidecarFeedId);
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, fileName);
        var lines = new List<string> { "ts,o,h,l,c,vol" };
        lines.AddRange(rows);
        File.WriteAllLines(filePath, lines);
    }

    private void WriteTopLevelSideCsv(string exchange, string symbol, string feedId, string fileName, string[] rows)
    {
        var dir = Path.Combine(_testDataRoot, exchange, symbol, feedId);
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, fileName);
        var lines = new List<string> { "ts,o,h,l,c,vol" };
        lines.AddRange(rows);
        File.WriteAllLines(filePath, lines);
    }

    private static long Ts(int year, int month, int day, int hour = 0, int min = 0) =>
        new DateTimeOffset(year, month, day, hour, min, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    // -------------------------------------------------------------------------
    // TimeBar happy path
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Load_SingleMonth_ReturnsCorrectBars()
    {
        WriteCandlesCsv("Binance", "BTCUSDT", 2024, 1, "1m",
        [
            $"{Ts(2024,1,1)},6743215,6745100,6741000,6744300,153240",
            $"{Ts(2024,1,1,0,1)},6743300,6745200,6741100,6744400,153300"
        ]);

        var series = await _loader.Load(
            TimeBarDescriptor("Binance", "BTCUSDT", "1m"),
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31), ct: Ct);

        Assert.Equal(2, series.Count);
        Assert.Equal(6743215L, series[0].Open);
        Assert.Equal(6745100L, series[0].High);
        Assert.Equal(6741000L, series[0].Low);
        Assert.Equal(6744300L, series[0].Close);
        Assert.Equal(153240L,  series[0].Volume);
        Assert.Equal(Ts(2024, 1, 1), series[0].TimestampMs);
    }

    [Fact]
    public async Task Load_MultiMonth_ReturnsAllBars()
    {
        WriteCandlesCsv("Binance", "BTCUSDT", 2024, 1, "1m",
            [$"{Ts(2024,1,15)},100,200,50,150,1000"]);
        WriteCandlesCsv("Binance", "BTCUSDT", 2024, 2, "1m",
            [$"{Ts(2024,2,10)},110,210,60,160,1100"]);

        var series = await _loader.Load(
            TimeBarDescriptor("Binance", "BTCUSDT", "1m"),
            new DateOnly(2024, 1, 1), new DateOnly(2024, 2, 28), ct: Ct);

        Assert.Equal(2, series.Count);
        Assert.Equal(100L, series[0].Open);
        Assert.Equal(110L, series[1].Open);
    }

    [Fact]
    public async Task Load_MultiMonth_SpanningYearBoundary()
    {
        WriteCandlesCsv("Binance", "BTCUSDT", 2024, 12, "1m",
            [$"{Ts(2024,12,31,23,59)},100,200,50,150,1000"]);
        WriteCandlesCsv("Binance", "BTCUSDT", 2025, 1, "1m",
            [$"{Ts(2025,1,1)},110,210,60,160,1100"]);

        var series = await _loader.Load(
            TimeBarDescriptor("Binance", "BTCUSDT", "1m"),
            new DateOnly(2024, 12, 1), new DateOnly(2025, 1, 31), ct: Ct);

        Assert.Equal(2, series.Count);
    }

    [Fact]
    public async Task Load_FiltersRowsOutsideDateRange()
    {
        WriteCandlesCsv("Binance", "BTCUSDT", 2024, 1, "1m",
        [
            $"{Ts(2024,1,1)},100,200,50,150,1000",
            $"{Ts(2024,1,15)},110,210,60,160,1100",
            $"{Ts(2024,1,31)},120,220,70,170,1200"
        ]);

        var series = await _loader.Load(
            TimeBarDescriptor("Binance", "BTCUSDT", "1m"),
            new DateOnly(2024, 1, 10), new DateOnly(2024, 1, 20), ct: Ct);

        var bar = Assert.Single(series);
        Assert.Equal(110L, bar.Open);
    }

    [Fact]
    public async Task Load_IntervalInFilename_1h()
    {
        WriteCandlesCsv("Binance", "BTCUSDT", 2024, 1, "1h",
            [$"{Ts(2024,1,1)},1000,1100,900,1050,5000"]);

        var series = await _loader.Load(
            TimeBarDescriptor("Binance", "BTCUSDT", "1h"),
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31), ct: Ct);

        var bar = Assert.Single(series);
        Assert.Equal(1000L, bar.Open);
    }

    [Fact]
    public async Task Load_MissingMonthFile_SkipsGracefully()
    {
        WriteCandlesCsv("Binance", "BTCUSDT", 2024, 1, "1m",
            [$"{Ts(2024,1,1)},100,200,50,150,1000"]);

        var series = await _loader.Load(
            TimeBarDescriptor("Binance", "BTCUSDT", "1m"),
            new DateOnly(2024, 1, 1), new DateOnly(2024, 2, 28), ct: Ct);

        Assert.Single(series);
    }

    [Fact]
    public async Task Load_NoDataInRange_ReturnsEmptySeries()
    {
        WriteCandlesCsv("Binance", "BTCUSDT", 2024, 1, "1m",
            [$"{Ts(2024,1,15)},100,200,50,150,1000"]);

        var series = await _loader.Load(
            TimeBarDescriptor("Binance", "BTCUSDT", "1m"),
            new DateOnly(2024, 6, 1), new DateOnly(2024, 6, 30), ct: Ct);

        Assert.Empty(series);
    }

    [Fact]
    public async Task Load_MissingFeedDirectory_ThrowsDirectoryNotFound()
    {
        var ex = await Assert.ThrowsAsync<DirectoryNotFoundException>(() => _loader.Load(
            AltBarDescriptor("Binance", "BTCUSDT", "EqV_1m_5M"),
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31), ct: Ct));

        Assert.Contains("EqV_1m_5M", ex.Message);
        Assert.Contains("Binance", ex.Message);
        Assert.Contains("BTCUSDT", ex.Message);
        Assert.Contains("Expected path", ex.Message);
    }

    [Fact]
    public async Task Load_TimeBar_PerFeedIdFilter_ExcludesOtherIntervals()
    {
        WriteCandlesCsv("Binance", "BTCUSDT", 2024, 1, "1m",
            [$"{Ts(2024,1,1)},1,1,1,1,100"]);
        WriteCandlesCsv("Binance", "BTCUSDT", 2024, 1, "5m",
            [$"{Ts(2024,1,1)},5,5,5,5,500"]);
        WriteCandlesCsv("Binance", "BTCUSDT", 2024, 1, "1h",
            [$"{Ts(2024,1,1)},60,60,60,60,6000"]);

        var series = await _loader.Load(
            TimeBarDescriptor("Binance", "BTCUSDT", "1m"),
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31), ct: Ct);

        var bar = Assert.Single(series);
        Assert.Equal(1L, bar.Open);
        Assert.Equal(100L, bar.Volume);
    }

    [Fact]
    public async Task Load_TimeBar_5mFeedId_DoesNotPickUp1m()
    {
        WriteCandlesCsv("Binance", "BTCUSDT", 2024, 1, "1m",
            [$"{Ts(2024,1,1)},1,1,1,1,100"]);
        WriteCandlesCsv("Binance", "BTCUSDT", 2024, 1, "5m",
            [$"{Ts(2024,1,1)},5,5,5,5,500"]);

        var series = await _loader.Load(
            TimeBarDescriptor("Binance", "BTCUSDT", "5m"),
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31), ct: Ct);

        var bar = Assert.Single(series);
        Assert.Equal(5L, bar.Open);
    }

    [Fact]
    public async Task Load_TimeBar_PartedPartition_IsIncluded()
    {
        // Mirror the AltBar parted layout for TimeBar files: bare month + .pNN parts.
        WriteCandlesCsv("Binance", "BTCUSDT", 2024, 1, "1m",
            [$"{Ts(2024,1,5)},100,200,50,150,1000"]);
        // .p01 / .p02 partitions for a different month — same feedId.
        var partedDir = Path.Combine(_testDataRoot, "Binance", "BTCUSDT", "candles");
        File.WriteAllLines(Path.Combine(partedDir, "2024-02_1m.p01.csv"),
            ["ts,o,h,l,c,vol", $"{Ts(2024,2,5)},110,210,60,160,1100"]);
        File.WriteAllLines(Path.Combine(partedDir, "2024-02_1m.p02.csv"),
            ["ts,o,h,l,c,vol", $"{Ts(2024,2,20)},120,220,70,170,1200"]);

        var series = await _loader.Load(
            TimeBarDescriptor("Binance", "BTCUSDT", "1m"),
            new DateOnly(2024, 1, 1), new DateOnly(2024, 2, 29), ct: Ct);

        Assert.Equal(3, series.Count);
        Assert.Equal(100L, series[0].Open);
        Assert.Equal(110L, series[1].Open);
        Assert.Equal(120L, series[2].Open);
    }

    [Fact]
    public async Task Load_TimeBar_LookAlikeFeedId_IsRejected()
    {
        // "_11m" must NOT match feedId "1m" — the underscore boundary is mandatory.
        WriteCandlesCsv("Binance", "BTCUSDT", 2024, 1, "11m",
            [$"{Ts(2024,1,1)},99,99,99,99,9999"]);
        WriteCandlesCsv("Binance", "BTCUSDT", 2024, 1, "1m",
            [$"{Ts(2024,1,1)},1,1,1,1,100"]);

        var series = await _loader.Load(
            TimeBarDescriptor("Binance", "BTCUSDT", "1m"),
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31), ct: Ct);

        var bar = Assert.Single(series);
        Assert.Equal(1L, bar.Open);
    }

    [Fact]
    public async Task Load_TimeBar_NonNumericPartSuffix_IsRejected()
    {
        // A stray "2024-01_1m.part.csv" or "_1m.pX.csv" must not be treated as a parted file.
        // Strict ".p\d+" guard rejects ".part" and ".pX" tails.
        WriteCandlesCsv("Binance", "BTCUSDT", 2024, 1, "1m",
            [$"{Ts(2024,1,1)},1,1,1,1,100"]);
        var dir = Path.Combine(_testDataRoot, "Binance", "BTCUSDT", "candles");
        File.WriteAllLines(Path.Combine(dir, "2024-01_1m.part.csv"),
            ["ts,o,h,l,c,vol", $"{Ts(2024,1,2)},9,9,9,9,9"]);

        var series = await _loader.Load(
            TimeBarDescriptor("Binance", "BTCUSDT", "1m"),
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31), ct: Ct);

        var bar = Assert.Single(series);
        Assert.Equal(1L, bar.Open);
    }

    // -------------------------------------------------------------------------
    // GetLastTimestamp
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetLastTimestamp_NoDirectory_ReturnsNull()
    {
        var result = await _loader.GetLastTimestamp(
            TimeBarDescriptor("Binance", "MISSING", "1m"), ct: Ct);
        Assert.Null(result);
    }

    [Fact]
    public async Task GetLastTimestamp_WithData_ReturnsLastTimestamp()
    {
        var ts1 = Ts(2024, 3, 1);
        var ts2 = Ts(2024, 3, 15, 12, 30);
        WriteCandlesCsv("Binance", "BTCUSDT", 2024, 3, "1m",
        [
            $"{ts1},100,200,50,150,1000",
            $"{ts2},110,210,60,160,1100"
        ]);

        var result = await _loader.GetLastTimestamp(
            TimeBarDescriptor("Binance", "BTCUSDT", "1m"), ct: Ct);
        Assert.NotNull(result);
        Assert.Equal(ts2, result!.Value.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task GetLastTimestamp_MultipleFiles_ReturnsLatest()
    {
        var tsJan = Ts(2024, 1, 31);
        var tsFeb = Ts(2024, 2, 29);
        WriteCandlesCsv("Binance", "BTCUSDT", 2024, 1, "1m", [$"{tsJan},100,200,50,150,1000"]);
        WriteCandlesCsv("Binance", "BTCUSDT", 2024, 2, "1m", [$"{tsFeb},110,210,60,160,1100"]);

        var result = await _loader.GetLastTimestamp(
            TimeBarDescriptor("Binance", "BTCUSDT", "1m"), ct: Ct);
        Assert.NotNull(result);
        Assert.Equal(tsFeb, result!.Value.ToUnixTimeMilliseconds());
    }

    // -------------------------------------------------------------------------
    // Malformed row handling
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Load_EmptyLines_SkipsGracefully()
    {
        WriteCandlesCsv("Binance", "BTCUSDT", 2024, 1, "1m",
        [
            "",
            $"{Ts(2024,1,1)},100,200,50,150,1000",
            "",
            $"{Ts(2024,1,1,0,1)},110,210,60,160,1100",
            ""
        ]);

        var series = await _loader.Load(
            TimeBarDescriptor("Binance", "BTCUSDT", "1m"),
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31), ct: Ct);

        Assert.Equal(2, series.Count);
    }

    [Fact]
    public async Task Load_FewerThanSixColumns_SkipsRow()
    {
        WriteCandlesCsv("Binance", "BTCUSDT", 2024, 1, "1m",
        [
            $"{Ts(2024,1,1)},100,200,50,150,1000",
            $"{Ts(2024,1,1,0,1)},110,210",
            $"{Ts(2024,1,1,0,2)},120,220,70,170,1200"
        ]);

        var series = await _loader.Load(
            TimeBarDescriptor("Binance", "BTCUSDT", "1m"),
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31), ct: Ct);

        Assert.Equal(2, series.Count);
        Assert.Equal(100L, series[0].Open);
        Assert.Equal(120L, series[1].Open);
    }

    [Fact]
    public async Task Load_NonNumericValues_SkipsRow()
    {
        WriteCandlesCsv("Binance", "BTCUSDT", 2024, 1, "1m",
        [
            $"{Ts(2024,1,1)},100,200,50,150,1000",
            $"{Ts(2024,1,1,0,1)},abc,210,60,160,1100",
            $"{Ts(2024,1,1,0,2)},120,220,70,170,1200"
        ]);

        var series = await _loader.Load(
            TimeBarDescriptor("Binance", "BTCUSDT", "1m"),
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31), ct: Ct);

        Assert.Equal(2, series.Count);
        Assert.Equal(100L, series[0].Open);
        Assert.Equal(120L, series[1].Open);
    }

    [Fact]
    public async Task Load_NonNumericTimestamp_SkipsRow()
    {
        WriteCandlesCsv("Binance", "BTCUSDT", 2024, 1, "1m",
        [
            $"{Ts(2024,1,1)},100,200,50,150,1000",
            "not-a-timestamp,110,210,60,160,1100",
            $"{Ts(2024,1,1,0,2)},120,220,70,170,1200"
        ]);

        var series = await _loader.Load(
            TimeBarDescriptor("Binance", "BTCUSDT", "1m"),
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31), ct: Ct);

        Assert.Equal(2, series.Count);
    }

    // -------------------------------------------------------------------------
    // AltBar
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Load_AltBar_LoadsFromAggregatedFeedDir()
    {
        WriteAggregatedCsv("Binance", "BTCUSDT", "EqV_1m_1000", "2026-04.csv",
            [$"{Ts(2026,4,1)},10,20,5,15,1000"]);
        WriteAggregatedCsv("Binance", "BTCUSDT", "EqV_1m_1000", "2026-05.csv",
            [$"{Ts(2026,5,1)},20,30,15,25,2000"]);

        var series = await _loader.Load(
            AltBarDescriptor("Binance", "BTCUSDT", "EqV_1m_1000"),
            new DateOnly(2026, 4, 1), new DateOnly(2026, 5, 31), ct: Ct);

        Assert.Equal(2, series.Count);
        Assert.Equal(10L, series[0].Open);
        Assert.Equal(20L, series[1].Open);
    }

    [Fact]
    public async Task Load_AltBar_PartNumberedPartitionsLexSortChronological()
    {
        WriteAggregatedCsv("Binance", "BTCUSDT", "EqV_1m_1000", "2026-04.csv",
            [$"{Ts(2026,4,15)},10,20,5,15,1000"]);
        WriteAggregatedCsv("Binance", "BTCUSDT", "EqV_1m_1000", "2026-05.p01.csv",
            [$"{Ts(2026,5,5)},20,30,15,25,2000"]);
        WriteAggregatedCsv("Binance", "BTCUSDT", "EqV_1m_1000", "2026-05.p02.csv",
            [$"{Ts(2026,5,20)},30,40,25,35,3000"]);

        var series = await _loader.Load(
            AltBarDescriptor("Binance", "BTCUSDT", "EqV_1m_1000"),
            new DateOnly(2026, 4, 1), new DateOnly(2026, 5, 31), ct: Ct);

        Assert.Equal(3, series.Count);
        Assert.Equal(10L, series[0].Open);
        Assert.Equal(20L, series[1].Open);
        Assert.Equal(30L, series[2].Open);
    }

    [Fact]
    public async Task Load_AltBar_BareAndPartNumberedSameMonth_Throws()
    {
        WriteAggregatedCsv("Binance", "BTCUSDT", "EqV_1m_1000", "2026-05.csv",
            [$"{Ts(2026,5,5)},10,20,5,15,1000"]);
        WriteAggregatedCsv("Binance", "BTCUSDT", "EqV_1m_1000", "2026-05.p01.csv",
            [$"{Ts(2026,5,20)},20,30,15,25,2000"]);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            _loader.Load(
                AltBarDescriptor("Binance", "BTCUSDT", "EqV_1m_1000"),
                new DateOnly(2026, 4, 1), new DateOnly(2026, 5, 31), ct: Ct));
        Assert.Contains("2026-05", ex.Message);
    }

    [Fact]
    public async Task GetLastTimestamp_AltBar_BareAndPartNumberedSameMonth_Throws()
    {
        WriteAggregatedCsv("Binance", "BTCUSDT", "EqV_1m_1000", "2026-05.csv",
            [$"{Ts(2026,5,5)},10,20,5,15,1000"]);
        WriteAggregatedCsv("Binance", "BTCUSDT", "EqV_1m_1000", "2026-05.p01.csv",
            [$"{Ts(2026,5,20)},20,30,15,25,2000"]);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            _loader.GetLastTimestamp(AltBarDescriptor("Binance", "BTCUSDT", "EqV_1m_1000"), ct: Ct));
    }

    // -------------------------------------------------------------------------
    // Tick + Side
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Load_Tick_RoutesToTicksDir()
    {
        WriteTicksCsv("Binance", "BTCUSDT_perp", 2026, 4, 15,
            [$"{Ts(2026,4,15,10,0)},100,100,100,100,5"]);
        WriteTicksCsv("Binance", "BTCUSDT_perp", 2026, 4, 16,
            [$"{Ts(2026,4,16,11,0)},110,110,110,110,7"]);

        var series = await _loader.Load(
            TickDescriptor("Binance", "BTCUSDT_perp"),
            new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 30), ct: Ct);

        Assert.Equal(2, series.Count);
        Assert.Equal(100L, series[0].Open);
        Assert.Equal(5L, series[0].Volume);
        Assert.Equal(110L, series[1].Open);
    }

    [Fact]
    public async Task Load_Side_Sidecar_RoutesToAggregatedFlowDir()
    {
        WriteSidecarCsv("Binance", "BTCUSDT_perp", "EqIV_ticks_500000.flow", "2026-04.csv",
            [$"{Ts(2026,4,1)},10,10,10,10,500"]);

        var series = await _loader.Load(
            SideDescriptor("Binance", "BTCUSDT_perp", "EqIV_ticks_500000.flow"),
            new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 30), ct: Ct);

        var bar = Assert.Single(series);
        Assert.Equal(10L, bar.Open);
        Assert.Equal(500L, bar.Volume);
    }

    [Fact]
    public async Task Load_Side_TopLevel_RoutesToAssetRootDir()
    {
        WriteTopLevelSideCsv("Binance", "BTCUSDT_perp", "funding-rate", "2026-04.csv",
            [$"{Ts(2026,4,1)},20,20,20,20,1000"]);

        var series = await _loader.Load(
            SideDescriptor("Binance", "BTCUSDT_perp", "funding-rate"),
            new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 30), ct: Ct);

        var bar = Assert.Single(series);
        Assert.Equal(20L, bar.Open);
        Assert.Equal(1000L, bar.Volume);
    }

    [Fact]
    public async Task Load_Side_Sidecar_DoesNotConflictWithParentBarDir()
    {
        WriteAggregatedCsv("Binance", "BTCUSDT_perp", "EqIV_ticks_500000", "2026-04.csv",
            [$"{Ts(2026,4,1)},999,999,999,999,99999"]);
        WriteSidecarCsv("Binance", "BTCUSDT_perp", "EqIV_ticks_500000.flow", "2026-04.csv",
            [$"{Ts(2026,4,1)},1,1,1,1,1"]);

        var sidecarSeries = await _loader.Load(
            SideDescriptor("Binance", "BTCUSDT_perp", "EqIV_ticks_500000.flow"),
            new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 30), ct: Ct);

        var bar = Assert.Single(sidecarSeries);
        Assert.Equal(1L, bar.Open);
        Assert.Equal(1L, bar.Volume);
    }
}
