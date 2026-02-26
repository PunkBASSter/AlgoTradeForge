using System.Diagnostics;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Tests.TestUtilities;
using AlgoTradeForge.Domain.Trading;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Events;

/// <summary>
/// Smoke test to detect gross performance regressions in the NullEventBus (no-op) path.
/// This is NOT a microbenchmark — it uses a generous time bound to catch regressions
/// without being flaky on slower CI machines.
/// </summary>
public class NullEventBusBenchmarkTests
{
    private const int BarCount = 500_000;
    private static readonly TimeSpan MaxAllowed = TimeSpan.FromSeconds(10);

    [Fact]
    public void NullEventBus_500K_Bars_CompletesWithinBound()
    {
        // Arrange: generate 500K bars
        var series = Generate500KBars();
        var sub = new DataSubscription(TestAssets.Aapl, TimeSpan.FromMinutes(1));
        var strategy = new NoOpStrategy(sub);
        var engine = new BacktestEngine(new BarMatcher(), new OrderValidator());
        var options = new BacktestOptions
        {
            InitialCash = 100_000L,
            Asset = TestAssets.Aapl,
            StartTime = DateTimeOffset.MinValue,
            EndTime = DateTimeOffset.MaxValue,
        };

        // Act
        var sw = Stopwatch.StartNew();
        var result = engine.Run([series], strategy, options);
        sw.Stop();

        // Assert
        Assert.Equal(BarCount, result.TotalBarsProcessed);
        Assert.True(sw.Elapsed < MaxAllowed,
            $"500K-bar backtest took {sw.Elapsed.TotalSeconds:F2}s, exceeding {MaxAllowed.TotalSeconds}s bound");
    }

    private static TimeSeries<Int64Bar> Generate500KBars()
    {
        var series = new TimeSeries<Int64Bar>();
        var startMs = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        const long stepMs = 60_000; // 1 minute

        for (var i = 0; i < BarCount; i++)
        {
            var price = 10000L + i % 1000;
            series.Add(new Int64Bar(
                startMs + i * stepMs,
                price,
                price + 200,
                price - 100,
                price + 100,
                1000));
        }

        return series;
    }

    private sealed class NoOpStrategy(DataSubscription subscription) : IInt64BarStrategy
    {
        public string Version => "1.0.0";
        public IList<DataSubscription> DataSubscriptions { get; } = [subscription];

        public void OnInit() { }
        public void OnBarStart(Int64Bar bar, DataSubscription subscription, IOrderContext orders) { }
        public void OnBarComplete(Int64Bar bar, DataSubscription subscription, IOrderContext orders) { }
        public void OnTrade(Fill fill, Order order) { }
    }
}
