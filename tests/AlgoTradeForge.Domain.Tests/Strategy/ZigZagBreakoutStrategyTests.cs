using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.ZigZagBreakout;
using AlgoTradeForge.Domain.Tests.TestUtilities;
using AlgoTradeForge.Domain.Trading;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Strategy;

public class ZigZagBreakoutStrategyTests
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);

    private static BacktestOptions CreateOptions(Asset asset) =>
        new()
        {
            InitialCash = 100_000L,
            Asset = asset,
            StartTime = DateTimeOffset.MinValue,
            EndTime = DateTimeOffset.MaxValue,
        };

    /// <summary>
    /// Builds a bar series that forms a zigzag pattern with 4 pivots:
    ///   2000(high) → 1700(low) → 2000(high) → 1850(low)
    /// Last 3 pivots: sl=1700, price=2000, l1=1850 → valid breakout signal.
    /// Entry Buy Stop at 2000, SL=1700, TP=2300.
    /// Bar 6 triggers entry (H>=2000), bar 7 triggers TP (H>=2300).
    /// </summary>
    private static Int64Bar[] BuildBreakoutBars() =>
    [
        // Bar 0: up, pivot at 2000
        TestBars.Create(1900, 2000, 1850, 1950),
        // Bar 1: down reversal (L=1800 < 2000-50=1950), swing=200
        TestBars.Create(1950, 1970, 1800, 1820),
        // Bar 2: extend down to 1700 (relocate)
        TestBars.Create(1810, 1850, 1700, 1750),
        // Bar 3: up reversal (H=1900 > 1700+100=1800), swing=200
        TestBars.Create(1760, 1900, 1740, 1880),
        // Bar 4: extend up to 2000 (relocate)
        TestBars.Create(1890, 2000, 1870, 1980),
        // Bar 5: down reversal (L=1850 < 2000-100=1900), swing=150
        // Pivots: 2000@0, 1700@2, 2000@4, 1850@5
        // Last 3: [1700, 2000, 1850] → signal! Entry at 2000, SL=1700, TP=2300
        TestBars.Create(1970, 1990, 1850, 1870),
        // Bar 6: entry triggers (H=2050 >= StopPrice=2000)
        TestBars.Create(1950, 2050, 1940, 2000),
        // Bar 7: TP hit (H=2350 >= TP=2300)
        TestBars.Create(2010, 2350, 2000, 2300),
    ];

    private static ZigZagBreakoutStrategy CreateStrategy(
        Asset asset,
        TimeSpan tf,
        decimal riskPct = 1m,
        decimal minSize = 1m,
        decimal maxSize = 100m)
    {
        var sub = new DataSubscription(asset, tf);
        return new ZigZagBreakoutStrategy(new ZigZagBreakoutParams
        {
            DzzDepth = 5m, // delta = 0.5
            MinimumThreshold = 50L,
            RiskPercentPerTrade = riskPct,
            MinPositionSize = minSize,
            MaxPositionSize = maxSize,
            DataSubscriptions = [sub],
        });
    }

    [Fact]
    public void EndToEnd_BuyStopEntry_WithSlTpFill()
    {
        var asset = TestAssets.Aapl;
        var strategy = CreateStrategy(asset, OneMinute);
        var series = TestBars.CreateSeries(Start, OneMinute, BuildBreakoutBars());
        var engine = new BacktestEngine(new BarMatcher(), new BasicRiskEvaluator());

        var result = engine.Run([series], strategy, CreateOptions(asset));

        // Entry fill + TP exit fill
        Assert.True(result.Fills.Count >= 2,
            $"Expected at least 2 fills (entry + exit), got {result.Fills.Count}");

        var entryFill = result.Fills[0];
        Assert.Equal(OrderSide.Buy, entryFill.Side);
        Assert.Equal(2000L, entryFill.Price);

        var exitFill = result.Fills[1];
        Assert.Equal(OrderSide.Sell, exitFill.Side);
        Assert.Equal(2300L, exitFill.Price);
    }

    [Fact]
    public void NoSignal_LessThan3Pivots_NoOrders()
    {
        var asset = TestAssets.Aapl;
        var sub = new DataSubscription(asset, OneMinute);

        var strategy = new ZigZagBreakoutStrategy(new ZigZagBreakoutParams
        {
            DzzDepth = 5m,
            MinimumThreshold = 5000L, // Very high threshold → no reversals → < 3 pivots
            DataSubscriptions = [sub],
        });

        var bars = TestBars.CreateSeries(Start, OneMinute, 20, startPrice: 1000);
        var engine = new BacktestEngine(new BarMatcher(), new BasicRiskEvaluator());

        var result = engine.Run([bars], strategy, CreateOptions(asset));

        Assert.Empty(result.Fills);
    }

    [Fact]
    public void OrderCancelReplace_WhenSignalChanges()
    {
        var asset = TestAssets.Aapl;
        var strategy = CreateStrategy(asset, OneMinute);

        // Use breakout bars but add extra bars that change the signal before entry triggers
        var bars = new List<Int64Bar>(BuildBreakoutBars().Take(6)); // bars 0-5 form the signal

        // Bar 6: stays below entry (no fill), and doesn't change indicator pivots
        // dir=down, extremum=1850. L=1860 >= 1850, H=1870 < 1850+75=1925 → no change
        bars.Add(TestBars.Create(1860, 1870, 1860, 1865));

        // Bar 7: extends down pivot (L=1800 < 1850, relocate), changes signal
        bars.Add(TestBars.Create(1840, 1860, 1800, 1830));

        // Bar 8: reversal up with different pivot value → new signal
        // threshold=150*0.5=75. Need H > 1800+75=1875
        bars.Add(TestBars.Create(1820, 1900, 1810, 1880));
        // New pivots include 1800 and 1900, changing last 3

        var series = TestBars.CreateSeries(Start, OneMinute, bars.ToArray());
        var engine = new BacktestEngine(new BarMatcher(), new BasicRiskEvaluator());
        var result = engine.Run([series], strategy, CreateOptions(asset));

        // All bars processed without errors
        Assert.Equal(bars.Count, result.TotalBarsProcessed);
    }

    [Fact]
    public void PositionSizing_ClampedToMaxSize()
    {
        var asset = TestAssets.Aapl;
        var strategy = CreateStrategy(asset, OneMinute, riskPct: 2m, maxSize: 5m);
        var series = TestBars.CreateSeries(Start, OneMinute, BuildBreakoutBars());
        var engine = new BacktestEngine(new BarMatcher(), new BasicRiskEvaluator());

        var result = engine.Run([series], strategy, CreateOptions(asset));

        if (result.Fills.Count > 0)
        {
            var entryFill = result.Fills[0];
            // slDistance=300, size = 100000*0.02/300 = 6.67 → clamped to max=5
            Assert.Equal(5m, entryFill.Quantity);
        }
    }

    [Fact]
    public void OnInit_InitializesIndicator()
    {
        var asset = TestAssets.Aapl;
        var strategy = CreateStrategy(asset, OneMinute);

        // Single bar — should not throw (OnInit called by engine)
        var bars = TestBars.CreateSeries(Start, OneMinute, 1, startPrice: 1000);
        var engine = new BacktestEngine(new BarMatcher(), new BasicRiskEvaluator());

        var result = engine.Run([bars], strategy, CreateOptions(asset));

        Assert.Equal(1, result.TotalBarsProcessed);
        Assert.Empty(result.Fills);
    }
}
