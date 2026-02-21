using System.Text;
using AlgoTradeForge.Application.Events;
using AlgoTradeForge.Domain.Events;
using AlgoTradeForge.Infrastructure.Events;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Xunit;

namespace AlgoTradeForge.Infrastructure.Tests.Events;

public class SqliteTradeDbWriterTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _runFolder;
    private readonly string _tradeDbPath;

    public SqliteTradeDbWriterTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"TradeDbTest_{Guid.NewGuid():N}");
        _runFolder = Path.Combine(_testRoot, "EventLogs", "TestStrat_v0_AAPL_2024-2024_000000_20240101T000000");
        Directory.CreateDirectory(_runFolder);
        _tradeDbPath = Path.Combine(_testRoot, "trades.sqlite");
    }

    public void Dispose()
    {
        // SQLite may hold locks briefly; allow GC + finalization
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
    }

    private SqliteTradeDbWriter CreateWriter()
    {
        var storageOpts = Options.Create(new EventLogStorageOptions
        {
            Root = Path.Combine(_testRoot, "EventLogs")
        });
        var pipelineOpts = Options.Create(new PostRunPipelineOptions
        {
            TradeDbPath = _tradeDbPath
        });
        return new SqliteTradeDbWriter(storageOpts, pipelineOpts);
    }

    private static RunIdentity MakeIdentity() => new()
    {
        StrategyName = "TestStrat",
        StrategyVersion = "0",
        AssetName = "AAPL",
        StartTime = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
        EndTime = new DateTimeOffset(2024, 12, 31, 0, 0, 0, TimeSpan.Zero),
        InitialCash = 100_000L,
        RunMode = ExportMode.Backtest,
        RunTimestamp = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
    };

    private static RunSummary MakeSummary() => new(1000, 105_000L, 2, TimeSpan.FromSeconds(3.5));

    private void WriteSampleJsonl()
    {
        var sb = new StringBuilder();
        sb.AppendLine("""{"ts":"2024-01-01T00:00:00+00:00","sq":1,"_t":"run.start","src":"engine","d":{"asset":"AAPL"}}""");
        sb.AppendLine("""{"ts":"2024-01-01T00:01:00+00:00","sq":2,"_t":"bar","src":"engine","d":{"o":1000,"h":1100,"l":900,"c":1050,"v":500}}""");
        sb.AppendLine("""{"ts":"2024-01-01T00:01:00+00:00","sq":3,"_t":"ord.place","src":"engine","d":{"orderId":1,"assetName":"AAPL","side":"buy","type":"market","quantity":10}}""");
        sb.AppendLine("""{"ts":"2024-01-01T00:01:00+00:00","sq":4,"_t":"risk","src":"engine","d":{"orderId":1,"verdict":"approved"}}""");
        sb.AppendLine("""{"ts":"2024-01-01T00:01:00+00:00","sq":5,"_t":"ord.fill","src":"engine","d":{"orderId":1,"assetName":"AAPL","side":"buy","price":1050,"quantity":10,"commission":5}}""");
        sb.AppendLine("""{"ts":"2024-01-01T00:01:00+00:00","sq":6,"_t":"pos","src":"engine","d":{"assetName":"AAPL","quantity":10,"averageEntryPrice":1050,"realizedPnl":0}}""");
        sb.AppendLine("""{"ts":"2024-01-01T00:02:00+00:00","sq":7,"_t":"bar","src":"engine","d":{"o":1050,"h":1150,"l":1000,"c":1100,"v":600}}""");
        sb.AppendLine("""{"ts":"2024-01-01T00:02:00+00:00","sq":8,"_t":"ord.place","src":"engine","d":{"orderId":2,"assetName":"AAPL","side":"sell","type":"limit","quantity":10,"limitPrice":1100}}""");
        sb.AppendLine("""{"ts":"2024-01-01T00:02:00+00:00","sq":9,"_t":"ord.fill","src":"engine","d":{"orderId":2,"assetName":"AAPL","side":"sell","price":1100,"quantity":10,"commission":5}}""");
        sb.AppendLine("""{"ts":"2024-01-01T00:02:00+00:00","sq":10,"_t":"pos","src":"engine","d":{"assetName":"AAPL","quantity":0,"averageEntryPrice":0,"realizedPnl":500}}""");
        sb.AppendLine("""{"ts":"2024-01-01T00:03:00+00:00","sq":11,"_t":"run.end","src":"engine","d":{"totalBars":2}}""");
        File.WriteAllText(Path.Combine(_runFolder, "events.jsonl"), sb.ToString());
    }

    [Fact]
    public void WriteFromJsonl_RunsTableContainsCorrectMetadata()
    {
        WriteSampleJsonl();
        var writer = CreateWriter();
        var identity = MakeIdentity();
        var summary = MakeSummary();

        writer.WriteFromJsonl(_runFolder, identity, summary);

        using var conn = new SqliteConnection($"Data Source={_tradeDbPath};Mode=ReadOnly");
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT strategy, version, asset, initial_cash, mode, total_bars, final_equity, total_fills FROM runs WHERE run_folder = $f";
        cmd.Parameters.AddWithValue("$f", Path.GetFileName(_runFolder));
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());

        Assert.Equal("TestStrat", reader.GetString(0));
        Assert.Equal("0", reader.GetString(1));
        Assert.Equal("AAPL", reader.GetString(2));
        Assert.Equal(100_000L, reader.GetInt64(3));
        Assert.Equal("Backtest", reader.GetString(4));
        Assert.Equal(1000, reader.GetInt32(5));
        Assert.Equal(105_000L, reader.GetInt64(6));
        Assert.Equal(2, reader.GetInt32(7));
    }

    [Fact]
    public void WriteFromJsonl_OrdersExtractedFromOrdPlaceEvents()
    {
        WriteSampleJsonl();
        var writer = CreateWriter();

        writer.WriteFromJsonl(_runFolder, MakeIdentity(), MakeSummary());

        using var conn = new SqliteConnection($"Data Source={_tradeDbPath};Mode=ReadOnly");
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT order_id, asset, side, type, quantity, status FROM orders ORDER BY order_id";
        using var reader = cmd.ExecuteReader();

        // Order 1: buy market, filled
        Assert.True(reader.Read());
        Assert.Equal(1, reader.GetInt64(0));
        Assert.Equal("AAPL", reader.GetString(1));
        Assert.Equal("buy", reader.GetString(2));
        Assert.Equal("market", reader.GetString(3));
        Assert.Equal("10", reader.GetString(4));
        Assert.Equal("filled", reader.GetString(5));

        // Order 2: sell limit, filled
        Assert.True(reader.Read());
        Assert.Equal(2, reader.GetInt64(0));
        Assert.Equal("sell", reader.GetString(2));
        Assert.Equal("limit", reader.GetString(3));
        Assert.Equal("filled", reader.GetString(5));

        Assert.False(reader.Read());
    }

    [Fact]
    public void WriteFromJsonl_TradesExtractedFromOrdFillEvents()
    {
        WriteSampleJsonl();
        var writer = CreateWriter();

        writer.WriteFromJsonl(_runFolder, MakeIdentity(), MakeSummary());

        using var conn = new SqliteConnection($"Data Source={_tradeDbPath};Mode=ReadOnly");
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT order_id, asset, side, price, quantity, commission FROM trades ORDER BY order_id";
        using var reader = cmd.ExecuteReader();

        // Fill 1
        Assert.True(reader.Read());
        Assert.Equal(1, reader.GetInt64(0));
        Assert.Equal("AAPL", reader.GetString(1));
        Assert.Equal("buy", reader.GetString(2));
        Assert.Equal(1050L, reader.GetInt64(3));
        Assert.Equal("10", reader.GetString(4));
        Assert.Equal(5L, reader.GetInt64(5));

        // Fill 2
        Assert.True(reader.Read());
        Assert.Equal(2, reader.GetInt64(0));
        Assert.Equal("sell", reader.GetString(2));
        Assert.Equal(1100L, reader.GetInt64(3));

        Assert.False(reader.Read());
    }

    [Fact]
    public async Task WriteFromJsonl_ConcurrentInserts_DontCorruptData()
    {
        // Create two separate run folders with different data
        var runFolder2 = Path.Combine(_testRoot, "EventLogs", "TestStrat_v0_AAPL_2024-2024_000000_20240102T000000");
        Directory.CreateDirectory(runFolder2);

        WriteSampleJsonl(); // writes to _runFolder

        var sb = new StringBuilder();
        sb.AppendLine("""{"ts":"2024-01-02T00:00:00+00:00","sq":1,"_t":"run.start","src":"engine","d":{"asset":"AAPL"}}""");
        sb.AppendLine("""{"ts":"2024-01-02T00:01:00+00:00","sq":2,"_t":"ord.place","src":"engine","d":{"orderId":1,"assetName":"AAPL","side":"buy","type":"market","quantity":5}}""");
        sb.AppendLine("""{"ts":"2024-01-02T00:01:00+00:00","sq":3,"_t":"ord.fill","src":"engine","d":{"orderId":1,"assetName":"AAPL","side":"buy","price":2000,"quantity":5,"commission":3}}""");
        sb.AppendLine("""{"ts":"2024-01-02T00:02:00+00:00","sq":4,"_t":"run.end","src":"engine","d":{"totalBars":1}}""");
        File.WriteAllText(Path.Combine(runFolder2, "events.jsonl"), sb.ToString());

        var identity1 = MakeIdentity();
        var identity2 = MakeIdentity() with
        {
            RunTimestamp = new DateTimeOffset(2024, 1, 2, 0, 0, 0, TimeSpan.Zero),
        };

        var summary1 = MakeSummary();
        var summary2 = new RunSummary(1, 102_000L, 1, TimeSpan.FromSeconds(1));

        // Run both inserts in parallel
        var t1 = Task.Run(() => CreateWriter().WriteFromJsonl(_runFolder, identity1, summary1));
        var t2 = Task.Run(() => CreateWriter().WriteFromJsonl(runFolder2, identity2, summary2));
        await Task.WhenAll(t1, t2);

        // Verify both runs exist
        using var conn = new SqliteConnection($"Data Source={_tradeDbPath};Mode=ReadOnly");
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM runs";
        Assert.Equal(2L, (long)cmd.ExecuteScalar()!);

        cmd.CommandText = "SELECT COUNT(*) FROM trades";
        var tradeCount = (long)cmd.ExecuteScalar()!;
        Assert.Equal(3L, tradeCount); // 2 from run1 + 1 from run2
    }

    [Fact]
    public void RebuildFromJsonl_ReplacesExistingRunData()
    {
        WriteSampleJsonl();
        var writer = CreateWriter();
        var identity = MakeIdentity();

        writer.WriteFromJsonl(_runFolder, identity, MakeSummary());

        // Verify 2 trades
        using (var conn = new SqliteConnection($"Data Source={_tradeDbPath};Mode=ReadOnly"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM trades";
            Assert.Equal(2L, (long)cmd.ExecuteScalar()!);
        }

        // Rebuild with fewer events
        var sb = new StringBuilder();
        sb.AppendLine("""{"ts":"2024-01-01T00:01:00+00:00","sq":1,"_t":"ord.place","src":"engine","d":{"orderId":1,"assetName":"AAPL","side":"buy","type":"market","quantity":10}}""");
        sb.AppendLine("""{"ts":"2024-01-01T00:01:00+00:00","sq":2,"_t":"ord.fill","src":"engine","d":{"orderId":1,"assetName":"AAPL","side":"buy","price":1050,"quantity":10,"commission":5}}""");
        File.WriteAllText(Path.Combine(_runFolder, "events.jsonl"), sb.ToString());

        writer.RebuildFromJsonl(_runFolder, identity, new RunSummary(500, 103_000L, 1, TimeSpan.FromSeconds(2)));

        using (var conn = new SqliteConnection($"Data Source={_tradeDbPath};Mode=ReadOnly"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM trades";
            Assert.Equal(1L, (long)cmd.ExecuteScalar()!);

            cmd.CommandText = "SELECT total_fills FROM runs";
            Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
        }
    }

    [Fact]
    public void WriteFromJsonl_WalModeEnabled()
    {
        WriteSampleJsonl();
        var writer = CreateWriter();

        writer.WriteFromJsonl(_runFolder, MakeIdentity(), MakeSummary());

        using var conn = new SqliteConnection($"Data Source={_tradeDbPath}");
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode";
        var mode = (string)cmd.ExecuteScalar()!;
        Assert.Equal("wal", mode);
    }
}
