using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Modules;
using AlgoTradeForge.Domain.Strategy.Modules.CrossAsset;
using AlgoTradeForge.Domain.Tests.TestUtilities;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Strategy.Modules.CrossAsset;

public sealed class CrossAssetModuleTests
{
    private static readonly DataSubscription Sub1 = new(TestAssets.BtcUsdt, TimeSpan.FromHours(1));
    private static readonly DataSubscription Sub2 = new(TestAssets.Aapl, TimeSpan.FromHours(1));

    private static IIndicatorFactory CreateMockFactory()
    {
        var factory = Substitute.For<IIndicatorFactory>();
        return factory;
    }

    private static CrossAssetModule CreateModule(int lookback = 20, double entryThreshold = 2.0, double exitThreshold = 0.5)
    {
        var module = new CrossAssetModule(new CrossAssetParams
        {
            LookbackPeriod = lookback,
            ZScoreEntryThreshold = entryThreshold,
            ZScoreExitThreshold = exitThreshold,
        });
        module.Initialize(CreateMockFactory(), Sub1, Sub2);
        return module;
    }

    [Fact]
    public void Update_WritesZScoreToContext()
    {
        var module = CreateModule(lookback: 5);
        var context = new StrategyContext();

        // Feed enough data to compute z-score
        for (var i = 0; i < 10; i++)
        {
            var bar1 = TestBars.AtPrice(10000 + i * 100, timestampMs: i * 60000);
            module.Update(bar1, Sub1, context);

            var bar2 = TestBars.AtPrice(5000 + i * 50, timestampMs: i * 60000);
            module.Update(bar2, Sub2, context);
        }

        Assert.True(context.Has("crossasset.zscore"), "z-score should be written to context");
    }

    [Fact]
    public void Update_WritesHedgeRatioToContext()
    {
        var module = CreateModule(lookback: 5);
        var context = new StrategyContext();

        for (var i = 0; i < 10; i++)
        {
            var bar1 = TestBars.AtPrice(10000 + i * 100, timestampMs: i * 60000);
            module.Update(bar1, Sub1, context);

            var bar2 = TestBars.AtPrice(5000 + i * 50, timestampMs: i * 60000);
            module.Update(bar2, Sub2, context);
        }

        Assert.True(context.Has("crossasset.hedge_ratio"), "hedge ratio should be written to context");
        var ratio = context.Get<double>("crossasset.hedge_ratio");
        Assert.True(ratio > 0, $"Hedge ratio should be positive for correlated series, got {ratio}");
    }

    [Fact]
    public void Update_WritesCointegrationStatusToContext()
    {
        var module = CreateModule(lookback: 5);
        var context = new StrategyContext();

        // Feed cointegrated (correlated) data
        for (var i = 0; i < 10; i++)
        {
            var bar1 = TestBars.AtPrice(10000 + i * 100, timestampMs: i * 60000);
            module.Update(bar1, Sub1, context);

            var bar2 = TestBars.AtPrice(5000 + i * 50, timestampMs: i * 60000);
            module.Update(bar2, Sub2, context);
        }

        Assert.True(context.Has("crossasset.cointegrated"), "cointegration status should be in context");
    }

    [Fact]
    public void Update_InsufficientData_NoContextKeys()
    {
        var module = CreateModule(lookback: 20);
        var context = new StrategyContext();

        // Only 2 bars — not enough for lookback of 20
        var bar1 = TestBars.AtPrice(10000);
        module.Update(bar1, Sub1, context);
        var bar2 = TestBars.AtPrice(5000);
        module.Update(bar2, Sub2, context);

        Assert.False(context.Has("crossasset.zscore"), "Not enough data for z-score");
    }

    [Fact]
    public void Update_DivergingSeries_ZScoreIsNonZero()
    {
        var module = CreateModule(lookback: 5);
        var context = new StrategyContext();

        // Correlated movement first
        for (var i = 0; i < 8; i++)
        {
            module.Update(TestBars.AtPrice(10000 + i * 100, timestampMs: i * 60000), Sub1, context);
            module.Update(TestBars.AtPrice(5000 + i * 50, timestampMs: i * 60000), Sub2, context);
        }

        // Then: A rises sharply while B stays flat → spread diverges
        for (var i = 8; i < 15; i++)
        {
            module.Update(TestBars.AtPrice(10800 + (i - 8) * 500, timestampMs: i * 60000), Sub1, context);
            module.Update(TestBars.AtPrice(5400, timestampMs: i * 60000), Sub2, context);
        }

        var zScore = context.Get<double>("crossasset.zscore");
        // After divergence, z-score should be significantly non-zero
        Assert.True(Math.Abs(zScore) > 0.5,
            $"Z-score should be significantly non-zero after divergence, got {zScore}");
    }
}
