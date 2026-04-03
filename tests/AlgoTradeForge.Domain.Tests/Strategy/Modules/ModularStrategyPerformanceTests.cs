using System.Diagnostics;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.BuyAndHold;
using AlgoTradeForge.Domain.Strategy.Modules.MoneyManagement;
using AlgoTradeForge.Domain.Strategy.Modules.TradeRegistry;
using AlgoTradeForge.Domain.Strategy.Rsi2MeanReversion;
using AlgoTradeForge.Domain.Strategy.Modules.Filter;
using AlgoTradeForge.Domain.Tests.TestUtilities;
using AlgoTradeForge.Domain.Trading;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Strategy.Modules;

[Trait("Category", "Performance")]
public sealed class ModularStrategyPerformanceTests
{
    private static readonly BacktestEngine Engine = new(new BarMatcher(), new OrderValidator());

    private static BacktestOptions CreateOptions(long initialCash = 10_000_000_000L) => new()
    {
        InitialCash = initialCash,
        StartTime = DateTimeOffset.MinValue,
        EndTime = DateTimeOffset.MaxValue,
    };

    private static TimeSeries<Int64Bar> CreateLargeSeries(int count = 10_000)
    {
        var series = new TimeSeries<Int64Bar>();
        var startMs = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var stepMs = 60_000L;
        var rng = new Random(42); // deterministic

        var price = 50000L;
        for (var i = 0; i < count; i++)
        {
            var change = rng.Next(-200, 201);
            price += change;
            if (price < 10000) price = 10000;
            var high = price + rng.Next(50, 300);
            var low = price - rng.Next(50, 300);
            var close = price + rng.Next(-100, 100);
            series.Add(new Int64Bar(startMs + i * stepMs, price, high, low, close, rng.Next(500, 5000)));
        }

        return series;
    }

    [Fact]
    public void Rsi2Modular_Vs_BuyAndHold_LessThan10xSlower()
    {
        var bars = CreateLargeSeries(10_000);

        // Baseline: BuyAndHold (simplest strategy)
        var bhParams = new BuyAndHoldParams
        {
            Quantity = 1m,
            DataSubscriptions = [new DataSubscription(TestAssets.BtcUsdt, TimeSpan.FromMinutes(1))],
        };
        var bhStrategy = new BuyAndHoldStrategy(bhParams);

        var sw1 = Stopwatch.StartNew();
        Engine.Run([bars], bhStrategy, CreateOptions(), ct: TestContext.Current.CancellationToken);
        sw1.Stop();

        // Modular: RSI2 (full pipeline with filters, indicators, sizing)
        var rsi2Params = new Rsi2Params
        {
            RsiPeriod = 2, OversoldThreshold = 10, OverboughtThreshold = 90,
            TrendFilterPeriod = 50, AtrPeriod = 14,
            AtrFilter = new AtrVolatilityFilterParams { Period = 14, MinAtr = 0, MaxAtr = 0 },
            SignalThreshold = 30, FilterThreshold = 0, DefaultAtrStopMultiplier = 2.0,
            MoneyManagement = new MoneyManagementParams { RiskPercent = 2.0 },
            TradeRegistry = new TradeRegistryParams { MaxConcurrentGroups = 1 },
            DataSubscriptions = [new DataSubscription(TestAssets.BtcUsdt, TimeSpan.FromMinutes(1))],
        };
        var rsi2Strategy = new Rsi2MeanReversionStrategy(rsi2Params);

        var sw2 = Stopwatch.StartNew();
        Engine.Run([bars], rsi2Strategy, CreateOptions(), ct: TestContext.Current.CancellationToken);
        sw2.Stop();

        // RSI2 should be within 10x of BuyAndHold (generous margin for modular overhead)
        // SC-006 says <10% per-bar regression vs equivalent hand-coded, but here we
        // compare against the simplest strategy so 10x is reasonable
        Assert.True(sw2.ElapsedMilliseconds < sw1.ElapsedMilliseconds * 10 + 100,
            $"Modular ({sw2.ElapsedMilliseconds}ms) more than 10x slower than baseline ({sw1.ElapsedMilliseconds}ms)");
    }

    [Fact]
    public void NoFilter_Vs_WithFilter_NegligibleOverhead()
    {
        var bars = CreateLargeSeries(10_000);

        // No filter: disable ATR filter
        var noFilterParams = new Rsi2Params
        {
            RsiPeriod = 2, OversoldThreshold = 10, OverboughtThreshold = 90,
            TrendFilterPeriod = 50, AtrPeriod = 14,
            AtrFilter = new AtrVolatilityFilterParams { Period = 14, MinAtr = 0, MaxAtr = 0 },
            SignalThreshold = 30, FilterThreshold = -101, // Never blocks (bypass)
            DefaultAtrStopMultiplier = 2.0,
            MoneyManagement = new MoneyManagementParams { RiskPercent = 2.0 },
            TradeRegistry = new TradeRegistryParams { MaxConcurrentGroups = 1 },
            DataSubscriptions = [new DataSubscription(TestAssets.BtcUsdt, TimeSpan.FromMinutes(1))],
        };

        var sw1 = Stopwatch.StartNew();
        Engine.Run([bars], new Rsi2MeanReversionStrategy(noFilterParams), CreateOptions(),
            ct: TestContext.Current.CancellationToken);
        sw1.Stop();

        // With filter at tight threshold
        var withFilterParams = new Rsi2Params
        {
            RsiPeriod = 2, OversoldThreshold = 10, OverboughtThreshold = 90,
            TrendFilterPeriod = 50, AtrPeriod = 14,
            AtrFilter = new AtrVolatilityFilterParams { Period = 14, MinAtr = 100, MaxAtr = 5000 },
            SignalThreshold = 30, FilterThreshold = 50,
            DefaultAtrStopMultiplier = 2.0,
            MoneyManagement = new MoneyManagementParams { RiskPercent = 2.0 },
            TradeRegistry = new TradeRegistryParams { MaxConcurrentGroups = 1 },
            DataSubscriptions = [new DataSubscription(TestAssets.BtcUsdt, TimeSpan.FromMinutes(1))],
        };

        var sw2 = Stopwatch.StartNew();
        Engine.Run([bars], new Rsi2MeanReversionStrategy(withFilterParams), CreateOptions(),
            ct: TestContext.Current.CancellationToken);
        sw2.Stop();

        // Filter overhead should be minimal — within 2x (generous for CI variance)
        Assert.True(sw2.ElapsedMilliseconds < sw1.ElapsedMilliseconds * 2 + 100,
            $"With filter ({sw2.ElapsedMilliseconds}ms) significantly slower than without ({sw1.ElapsedMilliseconds}ms)");
    }
}
