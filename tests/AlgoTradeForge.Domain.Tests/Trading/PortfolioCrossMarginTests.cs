using AlgoTradeForge.Domain.Trading;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Trading;

public class PortfolioCrossMarginTests
{
    private static readonly Asset SpotBtc = Asset.Crypto("BTCUSDT", "Binance", 2);
    private static readonly Asset PerpBtc = Asset.Future("BTCUSDT_PERP", "Binance", multiplier: 1m, tickSize: 0.01m, margin: 0.1m);
    private static readonly Asset EsMini = Asset.Future("ES", "CME", multiplier: 50m, tickSize: 0.25m, margin: 0.05m);

    #region ComputeUsedMargin

    [Fact]
    public void ComputeUsedMargin_NoPositions_ReturnsZero()
    {
        var portfolio = new Portfolio { InitialCash = 1_000_000L };
        portfolio.Initialize();

        Assert.Equal(0L, portfolio.ComputeUsedMargin());
    }

    [Fact]
    public void ComputeUsedMargin_SpotPosition_ReturnsZero()
    {
        var portfolio = new Portfolio { InitialCash = 1_000_000L };
        portfolio.Initialize();

        var fill = new Fill(1, SpotBtc, DateTimeOffset.UtcNow, 50_000_00L, 1m, OrderSide.Buy, 0L);
        portfolio.Apply(fill);

        // Spot positions (CashAndCarry) do NOT contribute to used margin
        Assert.Equal(0L, portfolio.ComputeUsedMargin());
    }

    [Fact]
    public void ComputeUsedMargin_FuturesLong_ReturnsInitialMargin()
    {
        var portfolio = new Portfolio { InitialCash = 1_000_000L };
        portfolio.Initialize();

        // Buy 1 BTC perp at 50000 with 10% margin requirement
        var fill = new Fill(1, PerpBtc, DateTimeOffset.UtcNow, 50_000L, 1m, OrderSide.Buy, 0L);
        portfolio.Apply(fill);

        // UsedMargin = |1| × 50000 × 1 × 0.1 = 5000
        Assert.Equal(5_000L, portfolio.ComputeUsedMargin());
    }

    [Fact]
    public void ComputeUsedMargin_FuturesShort_SameAsLong()
    {
        var portfolio = new Portfolio { InitialCash = 1_000_000L };
        portfolio.Initialize();

        // Sell 1 BTC perp at 50000 with 10% margin requirement
        var fill = new Fill(1, PerpBtc, DateTimeOffset.UtcNow, 50_000L, 1m, OrderSide.Sell, 0L);
        portfolio.Apply(fill);

        // UsedMargin = |-1| × 50000 × 1 × 0.1 = 5000 (symmetric with long)
        Assert.Equal(5_000L, portfolio.ComputeUsedMargin());
    }

    [Fact]
    public void ComputeUsedMargin_WithMultiplier_ScalesCorrectly()
    {
        var portfolio = new Portfolio { InitialCash = 10_000_000L };
        portfolio.Initialize();

        // Buy 2 ES at 5000 with multiplier 50 and 5% margin
        var fill = new Fill(1, EsMini, DateTimeOffset.UtcNow, 5_000L, 2m, OrderSide.Buy, 0L);
        portfolio.Apply(fill);

        // UsedMargin = 2 × 5000 × 50 × 0.05 = 25000
        Assert.Equal(25_000L, portfolio.ComputeUsedMargin());
    }

    [Fact]
    public void ComputeUsedMargin_AfterClose_ReturnsZero()
    {
        var portfolio = new Portfolio { InitialCash = 1_000_000L };
        portfolio.Initialize();

        portfolio.Apply(new Fill(1, PerpBtc, DateTimeOffset.UtcNow, 50_000L, 1m, OrderSide.Buy, 0L));
        Assert.Equal(5_000L, portfolio.ComputeUsedMargin());

        // Close position
        portfolio.Apply(new Fill(2, PerpBtc, DateTimeOffset.UtcNow, 51_000L, 1m, OrderSide.Sell, 0L));
        Assert.Equal(0L, portfolio.ComputeUsedMargin());
    }

    [Fact]
    public void ComputeUsedMargin_PartialClose_ReducesMargin()
    {
        var portfolio = new Portfolio { InitialCash = 1_000_000L };
        portfolio.Initialize();

        // Open 2 contracts
        portfolio.Apply(new Fill(1, PerpBtc, DateTimeOffset.UtcNow, 50_000L, 2m, OrderSide.Buy, 0L));
        Assert.Equal(10_000L, portfolio.ComputeUsedMargin()); // 2 × 50000 × 0.1

        // Close 1 contract
        portfolio.Apply(new Fill(2, PerpBtc, DateTimeOffset.UtcNow, 51_000L, 1m, OrderSide.Sell, 0L));
        Assert.Equal(5_000L, portfolio.ComputeUsedMargin()); // 1 × 50000 × 0.1
    }

    #endregion

    #region AvailableMargin

    [Fact]
    public void AvailableMargin_NoPositions_EqualsCash()
    {
        var portfolio = new Portfolio { InitialCash = 1_000_000L };
        portfolio.Initialize();

        Assert.Equal(1_000_000L, portfolio.AvailableMargin(50_000L));
    }

    [Fact]
    public void AvailableMargin_WithPosition_EquityMinusUsedMargin()
    {
        var portfolio = new Portfolio { InitialCash = 1_000_000L };
        portfolio.Initialize();

        // Buy 1 BTC perp at 50000 (margin settlement: only commission deducted)
        portfolio.Apply(new Fill(1, PerpBtc, DateTimeOffset.UtcNow, 50_000L, 1m, OrderSide.Buy, 100L));

        // Cash = 1_000_000 - 100 (commission) = 999_900
        // At price 51000: UnrealizedPnL = (51000 - 50000) × 1 × 1 = 1000
        // Equity = 999_900 + 1000 = 1_000_900
        // UsedMargin = 1 × 50000 × 1 × 0.1 = 5000
        // AvailableMargin = 1_000_900 - 5000 = 995_900
        Assert.Equal(995_900L, portfolio.AvailableMargin(51_000L));
    }

    #endregion

    #region Mixed spot + futures equity

    [Fact]
    public void Equity_MixedSpotAndFutures_CorrectPerPositionModel()
    {
        var portfolio = new Portfolio { InitialCash = 2_000_000L };
        portfolio.Initialize();

        // Spot buy: 1 BTC at 50000 → cash -= 50000
        portfolio.Apply(new Fill(1, SpotBtc, DateTimeOffset.UtcNow, 50_000_00L, 1m, OrderSide.Buy, 0L));

        // Futures buy: 1 PERP at 50000 → cash -= 0 (margin only)
        portfolio.Apply(new Fill(2, PerpBtc, DateTimeOffset.UtcNow, 50_000L, 1m, OrderSide.Buy, 0L));

        var prices = new Dictionary<string, long>
        {
            ["BTCUSDT"] = 52_000_00L,      // spot price in 2-decimal ticks
            ["BTCUSDT_PERP"] = 52_000L,     // perp price in 2-decimal ticks
        };

        // Cash = 2_000_000 - 5_000_000 (spot buy) = -3_000_000
        // Spot position value = 1 × 52_000_00 × 1 = 5_200_000
        // Futures position value = unrealizedPnL = (52000 - 50000) × 1 × 1 = 2000
        // Equity = -3_000_000 + 5_200_000 + 2_000 = 2_202_000
        Assert.Equal(2_202_000L, portfolio.Equity(prices));
    }

    [Fact]
    public void Equity_FuturesOnly_CashPlusUnrealizedPnl()
    {
        var portfolio = new Portfolio { InitialCash = 1_000_000L };
        portfolio.Initialize();

        portfolio.Apply(new Fill(1, PerpBtc, DateTimeOffset.UtcNow, 50_000L, 2m, OrderSide.Buy, 0L));

        // Cash unchanged (margin settlement). At price 48000:
        // UnrealizedPnL = (48000 - 50000) × 2 × 1 = -4000
        // Equity = 1_000_000 + (-4000) = 996_000
        Assert.Equal(996_000L, portfolio.Equity(48_000L));
    }

    #endregion

    #region Funding doesn't affect UsedMargin

    [Fact]
    public void CashAdjustment_DoesNotAffectUsedMargin()
    {
        var portfolio = new Portfolio { InitialCash = 1_000_000L };
        portfolio.Initialize();

        portfolio.Apply(new Fill(1, PerpBtc, DateTimeOffset.UtcNow, 50_000L, 1m, OrderSide.Buy, 0L));
        var marginBefore = portfolio.ComputeUsedMargin();

        // Funding cash adjustment
        portfolio.ApplyCashAdjustment(-500L);

        // UsedMargin unchanged (based on entry price, not cash)
        Assert.Equal(marginBefore, portfolio.ComputeUsedMargin());
        Assert.Equal(999_500L, portfolio.Cash);
    }

    #endregion
}
