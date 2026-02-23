using System.Text.Json;
using AlgoTradeForge.Application.Events;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.Events;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Trading;
using AlgoTradeForge.Infrastructure.Events;
using AlgoTradeForge.Infrastructure.IO;
using AlgoTradeForge.Infrastructure.Tests.TestUtilities;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AlgoTradeForge.Infrastructure.Tests.Events;

public class PostRunIntegrationTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);
    private static readonly Asset Aapl = Asset.Equity("AAPL", "NASDAQ");

    private readonly string _testRoot;
    private readonly string _tradeDbPath;

    public PostRunIntegrationTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"PostRunIntegration_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testRoot);
        _tradeDbPath = Path.Combine(_testRoot, "trades.sqlite");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
    }

    [Fact]
    public void FullBacktest_ProducesIndexSqliteAndTradesDb()
    {
        // Arrange — run engine through JSONL sink
        var identity = new RunIdentity
        {
            StrategyName = "IntegrationStrat",
            AssetName = "AAPL",
            StartTime = Start,
            EndTime = Start.AddMinutes(3),
            InitialCash = 100_000L,
            RunMode = ExportMode.Backtest,
            RunTimestamp = DateTimeOffset.UtcNow,
        };

        var eventLogRoot = Path.Combine(_testRoot, "EventLogs");
        var storageOptions = new EventLogStorageOptions { Root = eventLogRoot };
        using var sink = new JsonlFileSink(identity, storageOptions, new FileStorage());
        var bus = new EventBus(ExportMode.Backtest, [sink]);

        var sub = new DataSubscription(Aapl, OneMinute, IsExportable: true);
        var strategy = new BuyOnFirstBarStrategy(new BuyOnFirstBarParams { DataSubscriptions = [sub] });
        var bars = CreateSeries(Start, OneMinute, 3, startPrice: 1000);
        var engine = new BacktestEngine(new BarMatcher(), new BasicRiskEvaluator());

        var btOptions = new BacktestOptions
        {
            InitialCash = 100_000L,
            Asset = Aapl,
            StartTime = DateTimeOffset.MinValue,
            EndTime = DateTimeOffset.MaxValue,
        };

        // Act — run engine
        var result = engine.Run([bars], strategy, btOptions, bus: bus);

        var summary = new RunSummary(
            result.TotalBarsProcessed,
            result.EquityCurve.Count > 0 ? result.EquityCurve[^1].Value : 100_000L,
            result.Fills.Count,
            result.Duration);

        sink.WriteMeta(summary);
        sink.Dispose();

        // Run pipeline
        var indexBuilder = new SqliteEventIndexBuilder();
        var tradeDbWriter = new SqliteTradeDbWriter(
            Options.Create(storageOptions),
            Options.Create(new PostRunPipelineOptions { TradeDbPath = _tradeDbPath }));
        var pipeline = new PostRunPipeline(
            indexBuilder, tradeDbWriter,
            Options.Create(new PostRunPipelineOptions { BuildDebugIndex = true, TradeDbPath = _tradeDbPath }),
            NullLogger<PostRunPipeline>.Instance);

        var pipelineResult = pipeline.Execute(sink.RunFolderPath, identity, summary);

        // Assert
        Assert.True(pipelineResult.IndexBuilt);
        Assert.True(pipelineResult.TradesInserted);
        Assert.Null(pipelineResult.IndexError);
        Assert.Null(pipelineResult.TradesError);

        // Verify index.sqlite
        var indexPath = Path.Combine(sink.RunFolderPath, "index.sqlite");
        Assert.True(File.Exists(indexPath));

        using (var conn = new SqliteConnection($"Data Source={indexPath};Mode=ReadOnly;Pooling=False"))
        {
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM events";
            var eventCount = (long)cmd.ExecuteScalar()!;
            Assert.True(eventCount > 0, "Index should have events");

            // Query by type
            cmd.CommandText = "SELECT COUNT(*) FROM events WHERE _t = 'bar'";
            var barCount = (long)cmd.ExecuteScalar()!;
            Assert.Equal(3, barCount);

            // Query for order events
            cmd.CommandText = "SELECT COUNT(*) FROM events WHERE _t = 'ord.place'";
            var placeCount = (long)cmd.ExecuteScalar()!;
            Assert.True(placeCount >= 1);

            cmd.CommandText = "SELECT COUNT(*) FROM events WHERE _t = 'ord.fill'";
            var fillCount = (long)cmd.ExecuteScalar()!;
            Assert.True(fillCount >= 1);
        }

        // Verify trades.sqlite
        Assert.True(File.Exists(_tradeDbPath));

        using (var conn = new SqliteConnection($"Data Source={_tradeDbPath};Mode=ReadOnly;Pooling=False"))
        {
            conn.Open();

            using (var cmd1 = conn.CreateCommand())
            {
                cmd1.CommandText = "SELECT COUNT(*) FROM runs";
                Assert.Equal(1L, (long)cmd1.ExecuteScalar()!);
            }

            using (var cmd2 = conn.CreateCommand())
            {
                cmd2.CommandText = "SELECT strategy, asset FROM runs";
                using var reader = cmd2.ExecuteReader();
                Assert.True(reader.Read());
                Assert.Equal("IntegrationStrat", reader.GetString(0));
                Assert.Equal("AAPL", reader.GetString(1));
            }

            using (var cmd3 = conn.CreateCommand())
            {
                cmd3.CommandText = "SELECT COUNT(*) FROM orders";
                Assert.True((long)cmd3.ExecuteScalar()! >= 1);
            }

            using (var cmd4 = conn.CreateCommand())
            {
                cmd4.CommandText = "SELECT COUNT(*) FROM trades";
                Assert.True((long)cmd4.ExecuteScalar()! >= 1);
            }
        }
    }

    [Fact]
    public void CrashRecovery_RebuildFromJsonl_MatchesOriginal()
    {
        // Arrange — run engine through JSONL sink
        var identity = new RunIdentity
        {
            StrategyName = "RecoveryStrat",
            AssetName = "AAPL",
            StartTime = Start,
            EndTime = Start.AddMinutes(3),
            InitialCash = 100_000L,
            RunMode = ExportMode.Backtest,
            RunTimestamp = DateTimeOffset.UtcNow,
        };

        var eventLogRoot = Path.Combine(_testRoot, "EventLogs");
        var storageOptions = new EventLogStorageOptions { Root = eventLogRoot };
        using var sink = new JsonlFileSink(identity, storageOptions, new FileStorage());
        var bus = new EventBus(ExportMode.Backtest, [sink]);

        var sub = new DataSubscription(Aapl, OneMinute, IsExportable: true);
        var strategy = new BuyOnFirstBarStrategy(new BuyOnFirstBarParams { DataSubscriptions = [sub] });
        var bars = CreateSeries(Start, OneMinute, 3, startPrice: 1000);
        var engine = new BacktestEngine(new BarMatcher(), new BasicRiskEvaluator());

        var btOptions = new BacktestOptions
        {
            InitialCash = 100_000L,
            Asset = Aapl,
            StartTime = DateTimeOffset.MinValue,
            EndTime = DateTimeOffset.MaxValue,
        };

        var result = engine.Run([bars], strategy, btOptions, bus: bus);
        var summary = new RunSummary(
            result.TotalBarsProcessed,
            result.EquityCurve.Count > 0 ? result.EquityCurve[^1].Value : 100_000L,
            result.Fills.Count,
            result.Duration);
        sink.WriteMeta(summary);
        sink.Dispose();

        // Build original
        var indexBuilder = new SqliteEventIndexBuilder();
        indexBuilder.Build(sink.RunFolderPath);

        var indexPath = Path.Combine(sink.RunFolderPath, "index.sqlite");

        // Capture original event count
        long originalCount;
        using (var conn = new SqliteConnection($"Data Source={indexPath};Mode=ReadOnly;Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM events";
            originalCount = (long)cmd.ExecuteScalar()!;
        }

        // Delete the index (simulate crash)
        File.Delete(indexPath);
        Assert.False(File.Exists(indexPath));

        // Rebuild
        indexBuilder.Rebuild(sink.RunFolderPath);
        Assert.True(File.Exists(indexPath));

        // Verify rebuilt matches original
        using (var conn = new SqliteConnection($"Data Source={indexPath};Mode=ReadOnly;Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM events";
            var rebuiltCount = (long)cmd.ExecuteScalar()!;
            Assert.Equal(originalCount, rebuiltCount);
        }

        // Also verify trade DB rebuild
        var tradeDbWriter = new SqliteTradeDbWriter(
            Options.Create(storageOptions),
            Options.Create(new PostRunPipelineOptions { TradeDbPath = _tradeDbPath }));

        tradeDbWriter.WriteFromJsonl(sink.RunFolderPath, identity, summary);

        long originalTradeCount;
        using (var conn = new SqliteConnection($"Data Source={_tradeDbPath};Mode=ReadOnly;Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM trades";
            originalTradeCount = (long)cmd.ExecuteScalar()!;
        }

        // Rebuild trades
        tradeDbWriter.RebuildFromJsonl(sink.RunFolderPath, identity, summary);

        using (var conn = new SqliteConnection($"Data Source={_tradeDbPath};Mode=ReadOnly;Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM trades";
            var rebuiltTradeCount = (long)cmd.ExecuteScalar()!;
            Assert.Equal(originalTradeCount, rebuiltTradeCount);

            cmd.CommandText = "SELECT COUNT(*) FROM runs";
            Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static TimeSeries<Int64Bar> CreateSeries(
        DateTimeOffset startTime,
        TimeSpan step,
        int count,
        long startPrice = 10000,
        long priceIncrement = 100)
    {
        var series = new TimeSeries<Int64Bar>();
        var startMs = startTime.ToUnixTimeMilliseconds();
        var stepMs = (long)step.TotalMilliseconds;

        for (var i = 0; i < count; i++)
        {
            var price = startPrice + i * priceIncrement;
            series.Add(new Int64Bar(startMs + i * stepMs, price, price + 200, price - 100, price + 100, 1000));
        }

        return series;
    }

}
