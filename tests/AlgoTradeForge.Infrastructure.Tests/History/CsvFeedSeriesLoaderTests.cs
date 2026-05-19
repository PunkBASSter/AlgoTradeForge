using AlgoTradeForge.Infrastructure.History;
using AlgoTradeForge.Storage;
using Xunit;

namespace AlgoTradeForge.Infrastructure.Tests.History;

public class CsvFeedSeriesLoaderTests : IDisposable
{
    private readonly string _testDataRoot;
    private readonly CsvFeedSeriesLoader _loader = new(new LocalFileStorage());
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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
    public async Task Load_SingleMonth_ReturnsCorrectFeedSeries()
    {
        var ts1 = Ts(2024, 1, 1, 0);
        var ts2 = Ts(2024, 1, 1, 8);
        WriteCsv("Binance", "BTCUSDT_perp", "funding_rate", 2024, 1, "8h",
            "ts,rate",
            [
                $"{ts1},0.0001",
                $"{ts2},0.00015"
            ]);

        var result = await _loader.Load(
            _testDataRoot, "Binance", "BTCUSDT_perp", "funding_rate", "8h",
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31), ct: Ct);

        Assert.NotNull(result);
        Assert.Equal(2, result!.Count);
        Assert.Equal(ts1, result.Timestamps[0]);
        Assert.Equal(ts2, result.Timestamps[1]);
        Assert.Single(result.Columns);
        Assert.Equal(0.0001, result.Columns[0][0], precision: 8);
        Assert.Equal(0.00015, result.Columns[0][1], precision: 8);
    }

    [Fact]
    public async Task Load_MultipleColumns_RoundTrips()
    {
        var ts1 = Ts(2024, 1, 1);
        WriteCsv("Binance", "BTCUSDT", "oi", 2024, 1, null,
            "ts,oi_usd,oi_contracts",
            [
                $"{ts1},1000000.5,500.25"
            ]);

        var result = await _loader.Load(
            _testDataRoot, "Binance", "BTCUSDT", "oi", "",
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31), ct: Ct);

        Assert.NotNull(result);
        Assert.Equal(2, result!.ColumnCount);
        Assert.Equal(1000000.5, result.Columns[0][0], precision: 4);
        Assert.Equal(500.25, result.Columns[1][0], precision: 4);
    }

    [Fact]
    public async Task Load_MultiMonth_CombinesData()
    {
        WriteCsv("Binance", "BTCUSDT_perp", "funding_rate", 2024, 1, "8h",
            "ts,rate",
            [$"{Ts(2024,1,15)},0.0001"]);
        WriteCsv("Binance", "BTCUSDT_perp", "funding_rate", 2024, 2, "8h",
            "ts,rate",
            [$"{Ts(2024,2,15)},0.0002"]);

        var result = await _loader.Load(
            _testDataRoot, "Binance", "BTCUSDT_perp", "funding_rate", "8h",
            new DateOnly(2024, 1, 1), new DateOnly(2024, 2, 28), ct: Ct);

        Assert.NotNull(result);
        Assert.Equal(2, result!.Count);
    }

    [Fact]
    public async Task Load_NoFiles_ReturnsNull()
    {
        var result = await _loader.Load(
            _testDataRoot, "Binance", "BTCUSDT_perp", "funding_rate", "8h",
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31), ct: Ct);

        Assert.Null(result);
    }

    [Fact]
    public async Task Load_FiltersByDateRange()
    {
        var tsEarly = Ts(2024, 1, 1);
        var tsMid = Ts(2024, 1, 15);
        var tsLate = Ts(2024, 1, 31);
        WriteCsv("Binance", "BTCUSDT_perp", "funding_rate", 2024, 1, "8h",
            "ts,rate",
            [
                $"{tsEarly},0.0001",
                $"{tsMid},0.0002",
                $"{tsLate},0.0003"
            ]);

        var result = await _loader.Load(
            _testDataRoot, "Binance", "BTCUSDT_perp", "funding_rate", "8h",
            new DateOnly(2024, 1, 10), new DateOnly(2024, 1, 20), ct: Ct);

        Assert.NotNull(result);
        Assert.Single(result!.Timestamps);
        Assert.Equal(tsMid, result.Timestamps[0]);
        Assert.Equal(0.0002, result.Columns[0][0], precision: 8);
    }

    [Fact]
    public async Task Load_InvariantCulture_ParsesDecimalCorrectly()
    {
        var ts1 = Ts(2024, 1, 1);
        WriteCsv("Binance", "BTCUSDT_perp", "funding_rate", 2024, 1, null,
            "ts,rate",
            [
                $"{ts1},1.23456789"
            ]);

        var result = await _loader.Load(
            _testDataRoot, "Binance", "BTCUSDT_perp", "funding_rate", "",
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31), ct: Ct);

        Assert.NotNull(result);
        Assert.Equal(1.23456789, result!.Columns[0][0], precision: 8);
    }

    [Fact]
    public async Task Load_EmptyLines_SkipsGracefully()
    {
        var ts1 = Ts(2024, 1, 1);
        var ts2 = Ts(2024, 1, 1, 8);
        WriteCsv("Binance", "BTCUSDT_perp", "funding_rate", 2024, 1, "8h",
            "ts,rate",
            [
                "",
                $"{ts1},0.0001",
                "",
                $"{ts2},0.00015",
                ""
            ]);

        var result = await _loader.Load(
            _testDataRoot, "Binance", "BTCUSDT_perp", "funding_rate", "8h",
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31), ct: Ct);

        Assert.NotNull(result);
        Assert.Equal(2, result!.Count);
    }

    [Fact]
    public async Task Load_FewerColumnsThanHeader_ThrowsByDefault()
    {
        var ts1 = Ts(2024, 1, 1);
        var ts2 = Ts(2024, 1, 1, 8);
        WriteCsv("Binance", "BTCUSDT_perp", "oi", 2024, 1, null,
            "ts,oi_usd,oi_contracts",
            [
                $"{ts1},1000000.0,500.0",
                $"{ts2},2000000.0"
            ]);

        var ex = await Assert.ThrowsAsync<FormatException>(() => _loader.Load(
            _testDataRoot, "Binance", "BTCUSDT_perp", "oi", "",
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31), ct: Ct));

        Assert.Contains("nullable_columns", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Load_FewerColumnsThanHeader_NullableColumnsFillsWithNaN()
    {
        var ts1 = Ts(2024, 1, 1);
        var ts2 = Ts(2024, 1, 1, 8);
        WriteCsv("Binance", "BTCUSDT_perp", "oi", 2024, 1, null,
            "ts,oi_usd,oi_contracts",
            [
                $"{ts1},1000000.0,500.0",
                $"{ts2},2000000.0"
            ]);

        var result = await _loader.Load(
            _testDataRoot, "Binance", "BTCUSDT_perp", "oi", "",
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31),
            nullableColumns: true, ct: Ct);

        Assert.NotNull(result);
        Assert.Equal(2, result!.Count);
        Assert.Equal(500d, result.Columns[1][0]);
        Assert.True(double.IsNaN(result.Columns[1][1]),
            "Truncated row's missing column should be NaN under nullable_columns: true.");
    }

    [Fact]
    public async Task Load_EmptyCell_NullableColumnsFalse_Throws()
    {
        var ts = Ts(2024, 1, 1);
        WriteCsv("Binance", "BTCUSDT_perp", "EqIV_ticks_500000.flow", 2024, 1, null,
            "ts,signed_imbalance,buy_volume,sell_volume,realized_threshold",
            [
                $"{ts},,,,500000",
            ]);

        var ex = await Assert.ThrowsAsync<FormatException>(() => _loader.Load(
            _testDataRoot, "Binance", "BTCUSDT_perp", "EqIV_ticks_500000.flow", "",
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31),
            nullableColumns: false, ct: Ct));

        Assert.Contains("Empty/missing cell", ex.Message, StringComparison.Ordinal);
        Assert.Contains("EqIV_ticks_500000.flow", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Load_EmptyCell_NullableColumnsTrue_ParsesAsNaN()
    {
        var ts = Ts(2024, 1, 1);
        WriteCsv("Binance", "BTCUSDT_perp", "EqIV_ticks_500000.flow", 2024, 1, null,
            "ts,signed_imbalance,buy_volume,sell_volume,realized_threshold",
            [
                $"{ts},,,,500000",
                $"{ts + 1000},1.5,3.2,1.7,500000",
            ]);

        var result = await _loader.Load(
            _testDataRoot, "Binance", "BTCUSDT_perp", "EqIV_ticks_500000.flow", "",
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31),
            nullableColumns: true, ct: Ct);

        Assert.NotNull(result);
        Assert.Equal(2, result!.Count);
        Assert.Equal(4, result.ColumnCount);

        Assert.True(double.IsNaN(result.Columns[0][0]));
        Assert.True(double.IsNaN(result.Columns[1][0]));
        Assert.True(double.IsNaN(result.Columns[2][0]));
        Assert.Equal(500_000d, result.Columns[3][0]);

        Assert.Equal(1.5, result.Columns[0][1]);
        Assert.Equal(3.2, result.Columns[1][1]);
    }

    [Fact]
    public async Task Load_NonNumericValue_Throws()
    {
        var ts1 = Ts(2024, 1, 1);
        var ts2 = Ts(2024, 1, 1, 8);
        WriteCsv("Binance", "BTCUSDT_perp", "funding_rate", 2024, 1, "8h",
            "ts,rate",
            [
                $"{ts1},0.0001",
                $"{ts2},not-a-number"
            ]);

        var ex = await Assert.ThrowsAsync<FormatException>(() => _loader.Load(
            _testDataRoot, "Binance", "BTCUSDT_perp", "funding_rate", "8h",
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31), ct: Ct));

        Assert.Contains("Malformed numeric cell 'not-a-number'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("funding_rate", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Load_NonNumericValue_NullableColumnsTrue_StillThrows()
    {
        var ts1 = Ts(2024, 1, 1);
        WriteCsv("Binance", "BTCUSDT_perp", "EqIV_ticks_500000.flow", 2024, 1, null,
            "ts,signed_imbalance,buy_volume,sell_volume,realized_threshold",
            [
                $"{ts1},0.5,not-a-number,1.7,500000",
            ]);

        var ex = await Assert.ThrowsAsync<FormatException>(() => _loader.Load(
            _testDataRoot, "Binance", "BTCUSDT_perp", "EqIV_ticks_500000.flow", "",
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31),
            nullableColumns: true, ct: Ct));

        Assert.Contains("Malformed numeric cell 'not-a-number'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Load_NonNumericTimestamp_SkipsRow()
    {
        var ts1 = Ts(2024, 1, 1);
        WriteCsv("Binance", "BTCUSDT_perp", "funding_rate", 2024, 1, "8h",
            "ts,rate",
            [
                $"{ts1},0.0001",
                "invalid-ts,0.00015"
            ]);

        var result = await _loader.Load(
            _testDataRoot, "Binance", "BTCUSDT_perp", "funding_rate", "8h",
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31), ct: Ct);

        Assert.NotNull(result);
        Assert.Single(result!.Timestamps);
    }

    [Fact]
    public async Task Load_SingleColumnRow_SkipsRow()
    {
        var ts1 = Ts(2024, 1, 1);
        WriteCsv("Binance", "BTCUSDT_perp", "funding_rate", 2024, 1, "8h",
            "ts,rate",
            [
                $"{ts1},0.0001",
                "just-one-field"
            ]);

        var result = await _loader.Load(
            _testDataRoot, "Binance", "BTCUSDT_perp", "funding_rate", "8h",
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31), ct: Ct);

        Assert.NotNull(result);
        Assert.Single(result!.Timestamps);
    }
}
