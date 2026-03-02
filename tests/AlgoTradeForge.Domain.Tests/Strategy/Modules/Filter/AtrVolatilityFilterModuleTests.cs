using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Modules.Filter;
using AlgoTradeForge.Domain.Tests.TestUtilities;
using AlgoTradeForge.Domain.Trading;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Strategy.Modules.Filter;

public sealed class AtrVolatilityFilterModuleTests
{
    private static readonly DataSubscription DefaultSubscription =
        new(TestAssets.BtcUsdt, TimeSpan.FromMinutes(1));

    private static readonly AtrVolatilityFilterParams DefaultParams = new()
    {
        Period = 3,
        MinAtr = 10,
        MaxAtr = 100,
    };

    private static AtrVolatilityFilterModule CreateModule(AtrVolatilityFilterParams? p = null)
    {
        var module = new AtrVolatilityFilterModule(p ?? DefaultParams);
        module.Initialize(PassthroughIndicatorFactory.Instance, DefaultSubscription);
        return module;
    }

    private static List<Int64Bar> CreateBarsWithKnownAtr(int count, long range = 20)
    {
        var bars = new List<Int64Bar>();
        var basePrice = 1000L;
        for (var i = 0; i < count; i++)
        {
            var price = basePrice + i * 10;
            bars.Add(TestBars.Create(price, price + range, price - range / 2, price + range / 2));
        }
        return bars;
    }

    [Fact]
    public void Construction_WithValidParams_Succeeds()
    {
        var module = new AtrVolatilityFilterModule(DefaultParams);

        Assert.NotNull(module);
    }

    [Fact]
    public void IsAllowed_BeforeInitialize_ReturnsFalse()
    {
        var module = new AtrVolatilityFilterModule(DefaultParams);

        Assert.False(module.IsAllowed(TestBars.Flat(), OrderSide.Buy));
    }

    [Fact]
    public void IsAllowed_BeforeWarmup_ReturnsFalse()
    {
        var module = CreateModule();
        var bars = new List<Int64Bar>
        {
            TestBars.Create(1000, 1020, 980, 1010),
            TestBars.Create(1010, 1030, 990, 1020),
        };
        var series = TestBars.CreateSeries(bars.ToArray());
        module.ComputeIndicator(series);

        Assert.False(module.IsAllowed(bars[^1], OrderSide.Buy));
    }

    [Fact]
    public void IsAllowed_AtrWithinRange_ReturnsTrue()
    {
        var module = CreateModule(new AtrVolatilityFilterParams
        {
            Period = 3,
            MinAtr = 10,
            MaxAtr = 100,
        });

        var bars = CreateBarsWithKnownAtr(4, range: 30);
        var series = TestBars.CreateSeries(bars.ToArray());
        module.ComputeIndicator(series);

        Assert.True(module.IsAllowed(bars[^1], OrderSide.Buy));
    }

    [Fact]
    public void IsAllowed_AtrBelowMin_ReturnsFalse()
    {
        var module = CreateModule(new AtrVolatilityFilterParams
        {
            Period = 3,
            MinAtr = 1000,
            MaxAtr = 0,
        });

        var bars = CreateBarsWithKnownAtr(4, range: 10);
        var series = TestBars.CreateSeries(bars.ToArray());
        module.ComputeIndicator(series);

        Assert.False(module.IsAllowed(bars[^1], OrderSide.Buy));
    }

    [Fact]
    public void IsAllowed_AtrAboveMax_ReturnsFalse()
    {
        var module = CreateModule(new AtrVolatilityFilterParams
        {
            Period = 3,
            MinAtr = 0,
            MaxAtr = 5,
        });

        var bars = CreateBarsWithKnownAtr(4, range: 30);
        var series = TestBars.CreateSeries(bars.ToArray());
        module.ComputeIndicator(series);

        Assert.False(module.IsAllowed(bars[^1], OrderSide.Buy));
    }

    [Fact]
    public void IsAllowed_MaxAtrZero_NoUpperLimit()
    {
        var module = CreateModule(new AtrVolatilityFilterParams
        {
            Period = 3,
            MinAtr = 1,
            MaxAtr = 0,
        });

        var bars = CreateBarsWithKnownAtr(4, range: 5000);
        var series = TestBars.CreateSeries(bars.ToArray());
        module.ComputeIndicator(series);

        Assert.True(module.IsAllowed(bars[^1], OrderSide.Buy));
    }

    [Fact]
    public void IsAllowed_MinAtrZero_NoLowerLimit()
    {
        var module = CreateModule(new AtrVolatilityFilterParams
        {
            Period = 3,
            MinAtr = 0,
            MaxAtr = 100000,
        });

        var bars = CreateBarsWithKnownAtr(4, range: 2);
        var series = TestBars.CreateSeries(bars.ToArray());
        module.ComputeIndicator(series);

        Assert.True(module.IsAllowed(bars[^1], OrderSide.Buy));
    }

    [Fact]
    public void IsAllowed_DirectionAgnostic_SameResultForLongAndShort()
    {
        var module = CreateModule();
        var bars = CreateBarsWithKnownAtr(4, range: 30);
        var series = TestBars.CreateSeries(bars.ToArray());
        module.ComputeIndicator(series);

        var longResult = module.IsAllowed(bars[^1], OrderSide.Buy);
        var shortResult = module.IsAllowed(bars[^1], OrderSide.Sell);

        Assert.Equal(longResult, shortResult);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(10)]
    [InlineData(20)]
    public void ParameterSensitivity_DifferentPeriods(int period)
    {
        var module = CreateModule(new AtrVolatilityFilterParams
        {
            Period = period,
            MinAtr = 0,
            MaxAtr = 0,
        });

        var bars = CreateBarsWithKnownAtr(period + 2, range: 30);
        var series = TestBars.CreateSeries(bars.ToArray());
        module.ComputeIndicator(series);

        Assert.True(module.IsAllowed(bars[^1], OrderSide.Buy));
    }

    [Fact]
    public void DefaultParams_SaneDefaults()
    {
        var p = new AtrVolatilityFilterParams();

        Assert.Equal(14, p.Period);
        Assert.Equal(0L, p.MinAtr);
        Assert.Equal(0L, p.MaxAtr);
    }
}

file static class ModuleTestExtensions
{
    public static void ComputeIndicator(this AtrVolatilityFilterModule module, TimeSeries<Int64Bar> series)
    {
        module._indicator?.Compute(series);
    }
}
