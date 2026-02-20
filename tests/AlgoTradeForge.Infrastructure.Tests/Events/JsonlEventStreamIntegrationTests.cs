using System.Text.Json;
using AlgoTradeForge.Application.Events;
using AlgoTradeForge.Application.Indicators;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.Events;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Trading;
using AlgoTradeForge.Infrastructure.Events;
using Xunit;

namespace AlgoTradeForge.Infrastructure.Tests.Events;

public class JsonlEventStreamIntegrationTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);
    private static readonly Asset Aapl = Asset.Equity("AAPL", "NASDAQ");

    private readonly string _testRoot;

    public JsonlEventStreamIntegrationTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"JsonlIntegrationTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
    }

    [Fact]
    public void FullBacktestRun_ProducesCorrectJsonlEventStream()
    {
        // Arrange
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

        var options = new EventLogStorageOptions { Root = _testRoot };
        using var sink = new JsonlFileSink(identity, options);
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

        // Act
        engine.Run([bars], strategy, btOptions, bus: bus);
        sink.Dispose();

        // Assert — read the events.jsonl file
        var eventsPath = Path.Combine(sink.RunFolderPath, "events.jsonl");
        var lines = File.ReadAllLines(eventsPath);

        // Must have at least run.start, 3x bar, ord.place, risk, ord.fill, pos, run.end
        Assert.True(lines.Length >= 9, $"Expected at least 9 lines but got {lines.Length}");

        // First line is run.start
        var first = JsonDocument.Parse(lines[0]);
        Assert.Equal("run.start", first.RootElement.GetProperty("_t").GetString());

        // Last line is run.end
        var last = JsonDocument.Parse(lines[^1]);
        Assert.Equal("run.end", last.RootElement.GetProperty("_t").GetString());

        // Every line has required envelope fields: ts, sq, _t, src, d
        for (var i = 0; i < lines.Length; i++)
        {
            var doc = JsonDocument.Parse(lines[i]);
            var root = doc.RootElement;
            Assert.True(root.TryGetProperty("ts", out _), $"Line {i} missing 'ts'");
            Assert.True(root.TryGetProperty("sq", out _), $"Line {i} missing 'sq'");
            Assert.True(root.TryGetProperty("_t", out _), $"Line {i} missing '_t'");
            Assert.True(root.TryGetProperty("src", out _), $"Line {i} missing 'src'");
            Assert.True(root.TryGetProperty("d", out _), $"Line {i} missing 'd'");
        }

        // Collect type IDs
        var typeIds = lines.Select(l =>
        {
            var doc = JsonDocument.Parse(l);
            return doc.RootElement.GetProperty("_t").GetString()!;
        }).ToList();

        // Intermediate lines include bar, ord.place, ord.fill, pos, risk
        Assert.Contains("bar", typeIds);
        Assert.Contains("ord.place", typeIds);
        Assert.Contains("ord.fill", typeIds);
        Assert.Contains("pos", typeIds);
        Assert.Contains("risk", typeIds);

        // Sequence numbers are monotonically increasing starting from 1
        var sequences = lines.Select(l =>
        {
            var doc = JsonDocument.Parse(l);
            return doc.RootElement.GetProperty("sq").GetInt64();
        }).ToList();

        Assert.Equal(1, sequences[0]);
        for (var i = 1; i < sequences.Count; i++)
            Assert.True(sequences[i] > sequences[i - 1], $"Sequence numbers not increasing at index {i}");
    }

    [Fact]
    public void EventOrdering_OrdPlace_Before_OrdFill_Before_Pos()
    {
        // Arrange
        var identity = new RunIdentity
        {
            StrategyName = "OrderingStrat",
            AssetName = "AAPL",
            StartTime = Start,
            EndTime = Start.AddMinutes(1),
            InitialCash = 100_000L,
            RunMode = ExportMode.Backtest,
            RunTimestamp = DateTimeOffset.UtcNow,
        };

        var options = new EventLogStorageOptions { Root = _testRoot };
        using var sink = new JsonlFileSink(identity, options);
        var bus = new EventBus(ExportMode.Backtest, [sink]);

        var sub = new DataSubscription(Aapl, OneMinute, IsExportable: true);
        var strategy = new BuyOnFirstBarStrategy(new BuyOnFirstBarParams { DataSubscriptions = [sub] });

        var bars = CreateSeries(Start, OneMinute, 1, startPrice: 1000);
        var engine = new BacktestEngine(new BarMatcher(), new BasicRiskEvaluator());

        var btOptions = new BacktestOptions
        {
            InitialCash = 100_000L,
            Asset = Aapl,
            StartTime = DateTimeOffset.MinValue,
            EndTime = DateTimeOffset.MaxValue,
        };

        // Act
        engine.Run([bars], strategy, btOptions, bus: bus);
        sink.Dispose();

        // Assert
        var eventsPath = Path.Combine(sink.RunFolderPath, "events.jsonl");
        var typeIds = File.ReadAllLines(eventsPath)
            .Select(l => JsonDocument.Parse(l).RootElement.GetProperty("_t").GetString()!)
            .ToList();

        var placeIdx = typeIds.IndexOf("ord.place");
        var fillIdx = typeIds.IndexOf("ord.fill");
        var posIdx = typeIds.IndexOf("pos");

        Assert.True(placeIdx >= 0, "Expected ord.place event");
        Assert.True(fillIdx >= 0, "Expected ord.fill event");
        Assert.True(posIdx >= 0, "Expected pos event");
        Assert.True(placeIdx < fillIdx, "ord.place should precede ord.fill");
        Assert.True(fillIdx < posIdx, "ord.fill should precede pos");
    }

    [Fact]
    public void EmittingIndicatorFactory_ProducesIndEventsInJsonl()
    {
        // Arrange
        var identity = new RunIdentity
        {
            StrategyName = "IndicatorStrat",
            AssetName = "AAPL",
            StartTime = Start,
            EndTime = Start.AddMinutes(3),
            InitialCash = 100_000L,
            RunMode = ExportMode.Backtest,
            RunTimestamp = DateTimeOffset.UtcNow,
        };

        var options = new EventLogStorageOptions { Root = _testRoot };
        using var sink = new JsonlFileSink(identity, options);
        var bus = new EventBus(ExportMode.Backtest, [sink]);
        var indicatorFactory = new EmittingIndicatorFactory(bus);

        var sub = new DataSubscription(Aapl, OneMinute, IsExportable: true);
        var strategy = new IndicatorUsingStrategy(new IndicatorUsingParams { DataSubscriptions = [sub] }, indicatorFactory);

        var bars = CreateSeries(Start, OneMinute, 3, startPrice: 1000);
        var engine = new BacktestEngine(new BarMatcher(), new BasicRiskEvaluator());

        var btOptions = new BacktestOptions
        {
            InitialCash = 100_000L,
            Asset = Aapl,
            StartTime = DateTimeOffset.MinValue,
            EndTime = DateTimeOffset.MaxValue,
        };

        // Act
        engine.Run([bars], strategy, btOptions, bus: bus);
        sink.Dispose();

        // Assert
        var eventsPath = Path.Combine(sink.RunFolderPath, "events.jsonl");
        var typeIds = File.ReadAllLines(eventsPath)
            .Select(l => JsonDocument.Parse(l).RootElement.GetProperty("_t").GetString()!)
            .ToList();

        Assert.Contains("ind", typeIds);

        // Verify ind event has correct structure
        var indLines = File.ReadAllLines(eventsPath)
            .Where(l => JsonDocument.Parse(l).RootElement.GetProperty("_t").GetString() == "ind")
            .ToList();
        Assert.True(indLines.Count >= 3, $"Expected at least 3 ind events (one per bar), got {indLines.Count}");

        var firstInd = JsonDocument.Parse(indLines[0]);
        var data = firstInd.RootElement.GetProperty("d");
        Assert.Equal("DeltaZigZag", data.GetProperty("indicatorName").GetString());
    }

    [Fact]
    public void OptimizationPath_NoFactory_ZeroIndEvents()
    {
        // Arrange — backtest without indicator factory (optimization path)
        var identity = new RunIdentity
        {
            StrategyName = "NoIndStrat",
            AssetName = "AAPL",
            StartTime = Start,
            EndTime = Start.AddMinutes(3),
            InitialCash = 100_000L,
            RunMode = ExportMode.Backtest,
            RunTimestamp = DateTimeOffset.UtcNow,
        };

        var options = new EventLogStorageOptions { Root = _testRoot };
        using var sink = new JsonlFileSink(identity, options);
        var bus = new EventBus(ExportMode.Backtest, [sink]);

        var sub = new DataSubscription(Aapl, OneMinute, IsExportable: true);
        var strategy = new IndicatorUsingStrategy(new IndicatorUsingParams { DataSubscriptions = [sub] });

        var bars = CreateSeries(Start, OneMinute, 3, startPrice: 1000);
        var engine = new BacktestEngine(new BarMatcher(), new BasicRiskEvaluator());

        var btOptions = new BacktestOptions
        {
            InitialCash = 100_000L,
            Asset = Aapl,
            StartTime = DateTimeOffset.MinValue,
            EndTime = DateTimeOffset.MaxValue,
        };

        // Act — no indicatorFactory passed (passthrough default)
        engine.Run([bars], strategy, btOptions, bus: bus);
        sink.Dispose();

        // Assert
        var eventsPath = Path.Combine(sink.RunFolderPath, "events.jsonl");
        var typeIds = File.ReadAllLines(eventsPath)
            .Select(l => JsonDocument.Parse(l).RootElement.GetProperty("_t").GetString()!)
            .ToList();

        Assert.DoesNotContain("ind", typeIds);
        Assert.DoesNotContain("ind.mut", typeIds);
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

    private sealed class BuyOnFirstBarParams : StrategyParamsBase;

    private sealed class BuyOnFirstBarStrategy(BuyOnFirstBarParams p) : StrategyBase<BuyOnFirstBarParams>(p)
    {
        private bool _submitted;

        public override void OnBarStart(Int64Bar bar, DataSubscription subscription, IOrderContext orders)
        {
            if (_submitted) return;
            _submitted = true;
            orders.Submit(new Order
            {
                Id = 0,
                Asset = subscription.Asset,
                Side = OrderSide.Buy,
                Type = OrderType.Market,
                Quantity = 1m,
            });
        }
    }

    private sealed class IndicatorUsingParams : StrategyParamsBase;

    private sealed class IndicatorUsingStrategy(IndicatorUsingParams p, IIndicatorFactory? indicators = null) : StrategyBase<IndicatorUsingParams>(p, indicators)
    {
        private IIndicator<Int64Bar, long> _dzz = null!;
        private readonly List<Int64Bar> _barHistory = [];

        public override void OnInit()
        {
            _dzz = Indicators.Create(new DeltaZigZag(0.5m, 100L), DataSubscriptions[0]);
        }

        public override void OnBarComplete(Int64Bar bar, DataSubscription subscription, IOrderContext orders)
        {
            _barHistory.Add(bar);
            _dzz.Compute(_barHistory);
        }
    }
}
