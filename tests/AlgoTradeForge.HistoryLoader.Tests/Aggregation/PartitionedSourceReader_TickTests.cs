using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Application.Aggregation;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Aggregation;

/// <summary>
/// P2a-4: tick path on <see cref="PartitionedSourceReader"/> — daily-partitioned CSVs,
/// 5-column rows mapped to <see cref="SourceRecord"/> with OHLC=price, volume=qty.
/// </summary>
public sealed class PartitionedSourceReader_TickTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), $"PartitionedSourceReader_TickTests_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string TicksDir(string asset) =>
        Path.Combine(_tempDir, "binance", asset, "ticks");

    private void WriteTicks(string asset, string day, params (long ts, long price, long qty, int isBuyerMaker, long aggId)[] rows)
    {
        var dir = TicksDir(asset);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{day}.csv");
        using var sw = new StreamWriter(path);
        sw.WriteLine("ts,price,qty,is_buyer_maker,agg_id");
        foreach (var r in rows)
            sw.WriteLine($"{r.ts},{r.price},{r.qty},{r.isBuyerMaker},{r.aggId}");
    }

    private static long Ts(int year, int month, int day, int hour, int minute, int second) =>
        new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private DataFeedDescriptor TickSource(string asset) =>
        new(_tempDir, "binance", asset, FeedId: "ticks", Kind: DataFeedKind.Tick);

    [Fact]
    public void Read_SingleDay_YieldsAllRecords_OhlcEqualsPrice()
    {
        WriteTicks("BTCUSDT_perp", "2024-03-15",
            (Ts(2024, 3, 15, 12, 0, 0), 5000000, 100, 0, 1000),
            (Ts(2024, 3, 15, 12, 0, 1), 5000050, 200, 1, 1001),
            (Ts(2024, 3, 15, 12, 0, 2), 5000100, 50, 0, 1002));

        var reader = new PartitionedSourceReader();
        var records = reader.Read(TickSource("BTCUSDT_perp")).ToList();

        Assert.Equal(3, records.Count);
        // For ticks, OHLC all = price; volume = qty
        Assert.Equal(5000000, records[0].Open);
        Assert.Equal(5000000, records[0].High);
        Assert.Equal(5000000, records[0].Low);
        Assert.Equal(5000000, records[0].Close);
        Assert.Equal(100, records[0].Volume);

        Assert.Equal(5000100, records[2].Close);
        Assert.Equal(50, records[2].Volume);
    }

    [Fact]
    public void Read_AcrossDayBoundary_IsChronological()
    {
        WriteTicks("BTCUSDT_perp", "2024-03-16",
            (Ts(2024, 3, 16, 0, 0, 0), 5001000, 10, 0, 2000));
        WriteTicks("BTCUSDT_perp", "2024-03-15",
            (Ts(2024, 3, 15, 23, 59, 59), 5000900, 5, 1, 1999));

        var reader = new PartitionedSourceReader();
        var records = reader.Read(TickSource("BTCUSDT_perp")).ToList();

        Assert.Equal(2, records.Count);
        Assert.True(records[0].TsMs < records[1].TsMs);
        Assert.Equal(1999, records[0].Volume == 5 ? 1999 : 2000); // sanity
    }

    [Fact]
    public void Read_AcrossMonthBoundary_IsChronological()
    {
        WriteTicks("BTCUSDT_perp", "2024-04-01",
            (Ts(2024, 4, 1, 0, 0, 0), 5002000, 1, 0, 3000));
        WriteTicks("BTCUSDT_perp", "2024-03-31",
            (Ts(2024, 3, 31, 23, 59, 59), 5001000, 1, 0, 2999));
        WriteTicks("BTCUSDT_perp", "2024-03-30",
            (Ts(2024, 3, 30, 12, 0, 0), 5000000, 1, 0, 2998));

        var reader = new PartitionedSourceReader();
        var records = reader.Read(TickSource("BTCUSDT_perp")).ToList();

        Assert.Equal(3, records.Count);
        for (int i = 1; i < records.Count; i++)
            Assert.True(records[i].TsMs > records[i - 1].TsMs);
    }

    [Fact]
    public void Read_DateRangeFilter_RestrictsResults()
    {
        WriteTicks("BTCUSDT_perp", "2024-03-15",
            (Ts(2024, 3, 15, 12, 0, 0), 5000000, 1, 0, 100));
        WriteTicks("BTCUSDT_perp", "2024-03-20",
            (Ts(2024, 3, 20, 12, 0, 0), 5000500, 1, 0, 200));
        WriteTicks("BTCUSDT_perp", "2024-03-25",
            (Ts(2024, 3, 25, 12, 0, 0), 5001000, 1, 0, 300));

        var reader = new PartitionedSourceReader();
        var records = reader.Read(
            TickSource("BTCUSDT_perp"),
            from: new DateOnly(2024, 3, 18),
            to: new DateOnly(2024, 3, 22)).ToList();

        Assert.Single(records);
        Assert.Equal(5000500, records[0].Close);
    }

    [Fact]
    public void Read_NonexistentDir_YieldsEmpty()
    {
        var reader = new PartitionedSourceReader();
        var records = reader.Read(TickSource("DOESNOTEXIST")).ToList();
        Assert.Empty(records);
    }

    [Fact]
    public void Read_MalformedTickRow_Throws()
    {
        var dir = TicksDir("BTCUSDT_perp");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "2024-03-15.csv");
        File.WriteAllText(path, "ts,price,qty,is_buyer_maker,agg_id\n100,abc,1,0,500\n");

        var reader = new PartitionedSourceReader();
        Assert.Throws<FormatException>(() => reader.Read(TickSource("BTCUSDT_perp")).ToList());
    }
}
