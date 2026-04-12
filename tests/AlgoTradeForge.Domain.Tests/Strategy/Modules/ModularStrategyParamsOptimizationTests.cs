using System.Reflection;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Optimization.Attributes;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Modules;
using AlgoTradeForge.Domain.Strategy.Modules.MoneyManagement;
using AlgoTradeForge.Domain.Strategy.Modules.TradeRegistry;
using AlgoTradeForge.Domain.Strategy.Rsi2MeanReversion;
using AlgoTradeForge.Domain.Tests.TestUtilities;
using AlgoTradeForge.Domain.Trading;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Strategy.Modules;

public sealed class ModularStrategyParamsOptimizationTests
{
    // --- T067: Parameter discovery tests ---

    [Fact]
    public void Rsi2Params_TopLevelOptimizableProperties_AreDiscoverable()
    {
        var props = typeof(Rsi2Params)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<OptimizableAttribute>() is not null)
            .Select(p => p.Name)
            .ToHashSet();

        // Top-level Rsi2 params
        Assert.Contains("RsiPeriod", props);
        Assert.Contains("OversoldThreshold", props);
        Assert.Contains("OverboughtThreshold", props);
        Assert.Contains("TrendFilterPeriod", props);
        Assert.Contains("AtrPeriod", props);
    }

    [Fact]
    public void ModularStrategyParamsBase_ThresholdParams_AreDiscoverable()
    {
        var props = typeof(ModularStrategyParamsBase)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<OptimizableAttribute>() is not null)
            .Select(p => p.Name)
            .ToHashSet();

        Assert.Contains("FilterThreshold", props);
        Assert.Contains("SignalThreshold", props);
        Assert.Contains("ExitThreshold", props);
        Assert.Contains("DefaultAtrStopMultiplier", props);
    }

    [Fact]
    public void MoneyManagementParams_NestedOptimizableParams_AreDiscoverable()
    {
        var props = typeof(MoneyManagementParams)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<OptimizableAttribute>() is not null)
            .Select(p => p.Name)
            .ToHashSet();

        Assert.Contains("Method", props);
        Assert.Contains("RiskPercent", props);
        Assert.Contains("VolTarget", props);
        Assert.Contains("WinRate", props);
        Assert.Contains("PayoffRatio", props);
    }

    [Fact]
    public void TradeRegistryParams_MaxConcurrentGroups_IsDiscoverable()
    {
        var props = typeof(TradeRegistryParams)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<OptimizableAttribute>() is not null)
            .Select(p => p.Name)
            .ToHashSet();

        Assert.Contains("MaxConcurrentGroups", props);
    }

    [Fact]
    public void Rsi2Params_NestedMoneyManagement_IsAccessibleProperty()
    {
        var mmProp = typeof(Rsi2Params).GetProperty("MoneyManagement");
        Assert.NotNull(mmProp);
        Assert.Equal(typeof(MoneyManagementParams), mmProp!.PropertyType);
    }

    [Fact]
    public void Rsi2Params_HasStrategyKeyAttribute()
    {
        var attr = typeof(Rsi2MeanReversionStrategy).GetCustomAttribute<StrategyKeyAttribute>();
        Assert.NotNull(attr);
        Assert.Equal("RSI2-MeanReversion", attr!.Key);
    }

    // --- T068: Parallel trial isolation tests ---

    [Fact]
    public async Task ParallelTrials_HaveIndependentState()
    {
        var engine = new BacktestEngine(new BarMatcher(), new OrderValidator());
        var options = new BacktestOptions
        {
            InitialCash = 10_000_000_000L,
            StartTime = DateTimeOffset.MinValue,
            EndTime = DateTimeOffset.MaxValue,
        };

        var bars = CreateTestSeries();

        // Create two strategies with different params
        var params1 = new Rsi2Params
        {
            RsiPeriod = 2, OversoldThreshold = 10, OverboughtThreshold = 90,
            TrendFilterPeriod = 50, AtrPeriod = 14,
            MoneyManagement = new() { RiskPercent = 1.0 },
            TradeRegistry = new() { MaxConcurrentGroups = 1 },
            DataSubscriptions = [new DataSubscription(TestAssets.BtcUsdt, TimeSpan.FromMinutes(1))],
        };

        var params2 = new Rsi2Params
        {
            RsiPeriod = 5, OversoldThreshold = 20, OverboughtThreshold = 80,
            TrendFilterPeriod = 50, AtrPeriod = 14,
            MoneyManagement = new() { RiskPercent = 3.0 },
            TradeRegistry = new() { MaxConcurrentGroups = 2 },
            DataSubscriptions = [new DataSubscription(TestAssets.BtcUsdt, TimeSpan.FromMinutes(1))],
        };

        var strategy1 = new Rsi2MeanReversionStrategy(params1);
        var strategy2 = new Rsi2MeanReversionStrategy(params2);

        // Run in parallel — should not share state
        var task1 = Task.Run(() => engine.Run([bars], strategy1, options));
        var task2 = Task.Run(() => engine.Run([bars], strategy2, options));
        var results = await Task.WhenAll(task1, task2);
        var result1 = results[0];
        var result2 = results[1];

        // Both should complete without errors
        Assert.Equal(bars.Count, result1.TotalBarsProcessed);
        Assert.Equal(bars.Count, result2.TotalBarsProcessed);

        // Different params → potentially different results (proving no shared state)
        // At minimum, both ran independently without exceptions
    }

    [Fact]
    public void TwoStrategyInstances_HaveDifferentContexts()
    {
        var p = new Rsi2Params
        {
            RsiPeriod = 2, OversoldThreshold = 10, OverboughtThreshold = 90,
            TrendFilterPeriod = 50, AtrPeriod = 14,
            DataSubscriptions = [new DataSubscription(TestAssets.BtcUsdt, TimeSpan.FromMinutes(1))],
        };

        var s1 = new Rsi2MeanReversionStrategy(p);
        var s2 = new Rsi2MeanReversionStrategy(p);

        // After init, each should have its own context
        s1.OnInit();
        s2.OnInit();

        // Access context via reflection
        var ctxProp = typeof(ModularStrategyBase<Rsi2Params>)
            .GetProperty("Context", BindingFlags.NonPublic | BindingFlags.Instance)!;

        var ctx1 = ctxProp.GetValue(s1);
        var ctx2 = ctxProp.GetValue(s2);

        Assert.NotNull(ctx1);
        Assert.NotNull(ctx2);
        Assert.NotSame(ctx1, ctx2);
    }

    private static TimeSeries<Int64Bar> CreateTestSeries()
    {
        var series = new TimeSeries<Int64Bar>();
        var startMs = new DateTimeOffset(2024, 1, 1, 9, 30, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        for (var i = 0; i < 100; i++)
        {
            var price = 50000L + i * 50;
            series.Add(new Int64Bar(startMs + i * 60_000L, price, price + 100, price - 50, price + 30, 1000));
        }

        return series;
    }
}
