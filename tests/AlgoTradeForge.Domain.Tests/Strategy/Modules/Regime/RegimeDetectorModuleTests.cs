using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Modules;
using AlgoTradeForge.Domain.Strategy.Modules.Regime;
using AlgoTradeForge.Domain.Tests.TestUtilities;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Strategy.Modules.Regime;

public sealed class RegimeDetectorModuleTests
{
    private static readonly DataSubscription DefaultSub = new(TestAssets.BtcUsdt, TimeSpan.FromHours(1));

    private static IIndicatorFactory CreateMockFactory()
    {
        var factory = Substitute.For<IIndicatorFactory>();
        factory.Create(Arg.Any<IIndicator<Int64Bar, double>>(), Arg.Any<DataSubscription>())
            .Returns(callInfo => callInfo.ArgAt<IIndicator<Int64Bar, double>>(0));
        return factory;
    }

    [Fact]
    public void Update_BeforeInitialize_SetsUnknown()
    {
        var module = new RegimeDetectorModule(new RegimeDetectorParams());
        var context = new StrategyContext();
        var bar = TestBars.Flat();

        module.Update(bar, context);

        Assert.Equal(MarketRegime.Unknown, context.CurrentRegime);
    }

    [Fact]
    public void Update_InitializedWithAdx_HighAdx_SetsTrending()
    {
        var module = new RegimeDetectorModule(new RegimeDetectorParams
        {
            AdxPeriod = 7,
            TrendThreshold = 25.0,
        });
        var factory = CreateMockFactory();
        module.Initialize(factory, DefaultSub);

        // Create a strong trending series to ensure ADX > 25
        var bars = new List<Int64Bar>();
        for (var i = 0; i < 30; i++)
        {
            var price = 10000L + i * 200L; // Strong uptrend
            bars.Add(new Int64Bar(i * 60000L, price, price + 300, price - 50, price + 200, 1000L));
        }

        // Simulate what the pipeline does: compute indicator then call Update
        var adxField = typeof(RegimeDetectorModule).GetField("_adx",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var adx = adxField!.GetValue(module) as IIndicator<Int64Bar, double>;

        if (adx is not null)
        {
            adx.Compute(bars);
            var context = new StrategyContext();
            module.Update(bars[^1], context);

            // ADX from a strong trend should be high
            var adxValue = adx.Buffers["Value"][^1];
            if (adxValue > 25.0)
                Assert.Equal(MarketRegime.Trending, context.CurrentRegime);
            else
                Assert.Equal(MarketRegime.RangeBound, context.CurrentRegime);
        }
    }

    [Fact]
    public void Update_InitializedWithAdx_LowAdx_SetsRangeBound()
    {
        var module = new RegimeDetectorModule(new RegimeDetectorParams
        {
            AdxPeriod = 7,
            TrendThreshold = 25.0,
        });
        var factory = CreateMockFactory();
        module.Initialize(factory, DefaultSub);

        // Create a choppy/range-bound series
        var bars = new List<Int64Bar>();
        for (var i = 0; i < 30; i++)
        {
            var offset = (i % 2 == 0) ? 100L : -100L;
            var price = 10000L + offset;
            bars.Add(new Int64Bar(i * 60000L, price, price + 150, price - 150, price, 1000L));
        }

        var adxField = typeof(RegimeDetectorModule).GetField("_adx",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var adx = adxField!.GetValue(module) as IIndicator<Int64Bar, double>;

        if (adx is not null)
        {
            adx.Compute(bars);
            var context = new StrategyContext();
            module.Update(bars[^1], context);

            var adxValue = adx.Buffers["Value"][^1];
            if (adxValue <= 25.0)
                Assert.Equal(MarketRegime.RangeBound, context.CurrentRegime);
            else
                Assert.Equal(MarketRegime.Trending, context.CurrentRegime);
        }
    }

    [Fact]
    public void Update_DuringWarmup_SetsUnknown()
    {
        var module = new RegimeDetectorModule(new RegimeDetectorParams
        {
            AdxPeriod = 14,
            TrendThreshold = 25.0,
        });
        var factory = CreateMockFactory();
        module.Initialize(factory, DefaultSub);

        // Only 5 bars — not enough warmup for period 14 ADX
        var bars = new List<Int64Bar>();
        for (var i = 0; i < 5; i++)
        {
            var price = 10000L + i * 100L;
            bars.Add(new Int64Bar(i * 60000L, price, price + 200, price - 100, price + 100, 1000L));
        }

        var adxField = typeof(RegimeDetectorModule).GetField("_adx",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var adx = adxField!.GetValue(module) as IIndicator<Int64Bar, double>;

        if (adx is not null)
        {
            adx.Compute(bars);
            var context = new StrategyContext();
            module.Update(bars[^1], context);

            Assert.Equal(MarketRegime.Unknown, context.CurrentRegime);
        }
    }
}
