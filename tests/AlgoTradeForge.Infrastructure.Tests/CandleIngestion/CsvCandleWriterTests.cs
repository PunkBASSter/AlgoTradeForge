using AlgoTradeForge.Application.CandleIngestion;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.CandleIngestor.Storage;
using Xunit;

namespace AlgoTradeForge.Infrastructure.Tests.CandleIngestion;

public class CsvCandleWriterTests : IDisposable
{
    private readonly string _testDataRoot;

    public CsvCandleWriterTests()
    {
        _testDataRoot = Path.Combine(Path.GetTempPath(), $"CsvWriterTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDataRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDataRoot))
            Directory.Delete(_testDataRoot, recursive: true);
    }

    private CsvCandleWriter CreateWriter() => new(_testDataRoot);

    private static RawCandle MakeCandle(DateTimeOffset timestamp, decimal price = 100m, decimal volume = 500m) =>
        new(timestamp, price, price + 1m, price - 1m, price + 0.5m, volume);

    [Fact]
    public void WriteCandle_CreatesDirectoryStructure()
    {
        using var writer = CreateWriter();
        var candle = MakeCandle(new DateTimeOffset(2024, 3, 15, 10, 0, 0, TimeSpan.Zero));

        writer.WriteCandle(candle, "Binance", "BTCUSDT", 2);
        writer.Flush();

        var expected = Path.Combine(_testDataRoot, "Binance", "BTCUSDT", "2024", "2024-03.csv");
        Assert.True(File.Exists(expected));
    }

    [Fact]
    public void WriteCandle_NewFile_WritesHeaderRow()
    {
        var writer = CreateWriter();
        var candle = MakeCandle(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));

        writer.WriteCandle(candle, "Binance", "BTCUSDT", 2);
        writer.Dispose();

        var path = Path.Combine(_testDataRoot, "Binance", "BTCUSDT", "2024", "2024-01.csv");
        var lines = File.ReadAllLines(path);
        Assert.Equal("Timestamp,Open,High,Low,Close,Volume", lines[0]);
    }

    [Fact]
    public void WriteCandle_IntegerConversion_IsCorrect()
    {
        var writer = CreateWriter();
        var candle = new RawCandle(
            new DateTimeOffset(2024, 1, 15, 0, 0, 0, TimeSpan.Zero),
            67432.15m, 67451.00m, 67410.00m, 67443.00m, 1532.40m);

        writer.WriteCandle(candle, "Binance", "BTCUSDT", 2);
        writer.Dispose();

        var path = Path.Combine(_testDataRoot, "Binance", "BTCUSDT", "2024", "2024-01.csv");
        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);

        var parts = lines[1].Split(',');
        Assert.Equal("6743215", parts[1]);
        Assert.Equal("6745100", parts[2]);
        Assert.Equal("6741000", parts[3]);
        Assert.Equal("6744300", parts[4]);
        Assert.Equal("153240", parts[5]);
    }

    [Fact]
    public void WriteCandle_MonthBoundary_RoutesToCorrectPartition()
    {
        using var writer = CreateWriter();
        var jan = MakeCandle(new DateTimeOffset(2024, 1, 31, 23, 59, 0, TimeSpan.Zero));
        var feb = MakeCandle(new DateTimeOffset(2024, 2, 1, 0, 0, 0, TimeSpan.Zero));

        writer.WriteCandle(jan, "Binance", "BTCUSDT", 2);
        writer.WriteCandle(feb, "Binance", "BTCUSDT", 2);
        writer.Flush();

        Assert.True(File.Exists(Path.Combine(_testDataRoot, "Binance", "BTCUSDT", "2024", "2024-01.csv")));
        Assert.True(File.Exists(Path.Combine(_testDataRoot, "Binance", "BTCUSDT", "2024", "2024-02.csv")));
    }

    [Fact]
    public void WriteCandle_Append_DoesNotDuplicateHeader()
    {
        var writer1 = CreateWriter();
        var candle1 = MakeCandle(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
        writer1.WriteCandle(candle1, "Binance", "BTCUSDT", 2);
        writer1.Dispose();

        var writer2 = CreateWriter();
        var candle2 = MakeCandle(new DateTimeOffset(2024, 1, 1, 0, 1, 0, TimeSpan.Zero));
        writer2.WriteCandle(candle2, "Binance", "BTCUSDT", 2);
        writer2.Dispose();

        var path = Path.Combine(_testDataRoot, "Binance", "BTCUSDT", "2024", "2024-01.csv");
        var lines = File.ReadAllLines(path);
        var headerCount = lines.Count(l => l.StartsWith("Timestamp"));
        Assert.Equal(1, headerCount);
    }

    [Fact]
    public void WriteCandle_DuplicateTimestamp_IsSkipped()
    {
        var writer = CreateWriter();
        var ts = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var candle1 = MakeCandle(ts, 100m);
        var candle2 = MakeCandle(ts, 200m);

        writer.WriteCandle(candle1, "Binance", "BTCUSDT", 2);
        writer.WriteCandle(candle2, "Binance", "BTCUSDT", 2);
        writer.Dispose();

        var path = Path.Combine(_testDataRoot, "Binance", "BTCUSDT", "2024", "2024-01.csv");
        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length); // header + 1 data row
    }

    [Fact]
    public void GetLastTimestamp_NoData_ReturnsNull()
    {
        using var writer = CreateWriter();
        var result = writer.GetLastTimestamp("Binance", "BTCUSDT");
        Assert.Null(result);
    }

    [Fact]
    public void GetLastTimestamp_WithData_ReturnsLastTimestamp()
    {
        using var writer = CreateWriter();
        var ts1 = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var ts2 = new DateTimeOffset(2024, 1, 1, 0, 1, 0, TimeSpan.Zero);
        writer.WriteCandle(MakeCandle(ts1), "Binance", "BTCUSDT", 2);
        writer.WriteCandle(MakeCandle(ts2), "Binance", "BTCUSDT", 2);
        writer.Flush();
        writer.Dispose();

        using var reader = CreateWriter();
        var result = reader.GetLastTimestamp("Binance", "BTCUSDT");
        Assert.NotNull(result);
        Assert.Equal(ts2, result.Value);
    }
}
