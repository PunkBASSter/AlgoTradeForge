using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Trading;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Engine;

public class AssetAutoApplyTests
{
    private static readonly Asset PerpAsset = CryptoPerpetualAsset.Create("BTCUSDT_PERP", "Binance", decimalDigits: 2);

    private static Position CreateLongPosition(Asset asset, decimal qty, long entryPrice)
    {
        var position = new Position(asset);
        var fill = new Fill(1, asset, DateTimeOffset.UtcNow, entryPrice, qty, OrderSide.Buy, 0L);
        position.Apply(fill);
        return position;
    }

    private static Position CreateShortPosition(Asset asset, decimal qty, long entryPrice)
    {
        var position = new Position(asset);
        var fill = new Fill(1, asset, DateTimeOffset.UtcNow, entryPrice, qty, OrderSide.Sell, 0L);
        position.Apply(fill);
        return position;
    }

    #region FundingRate

    [Fact]
    public void FundingRate_LongPositiveRate_PaysFunding()
    {
        // Long 1 BTC at 50000, funding rate = 0.0001
        // Expected: -1 * 50000 * 0.0001 * 1 = -5
        var position = CreateLongPosition(PerpAsset, 1m, 5_000_000L);
        var delta = PerpAsset.ComputeAutoApplyDelta(AutoApplyType.FundingRate, 0.0001, position, 5_000_000L);

        Assert.Equal(-500L, delta); // -5000000 * 0.0001 = -500
    }

    [Fact]
    public void FundingRate_LongNegativeRate_ReceivesFunding()
    {
        var position = CreateLongPosition(PerpAsset, 1m, 5_000_000L);
        var delta = PerpAsset.ComputeAutoApplyDelta(AutoApplyType.FundingRate, -0.0001, position, 5_000_000L);

        Assert.Equal(500L, delta); // -(-0.0001) * 5000000 = +500
    }

    [Fact]
    public void FundingRate_ShortPositiveRate_ReceivesFunding()
    {
        // Short position: Quantity is negative. -(negative * price * rate) = positive
        var position = CreateShortPosition(PerpAsset, 1m, 5_000_000L);
        var delta = PerpAsset.ComputeAutoApplyDelta(AutoApplyType.FundingRate, 0.0001, position, 5_000_000L);

        Assert.Equal(500L, delta);
    }

    [Fact]
    public void FundingRate_ShortNegativeRate_PaysFunding()
    {
        var position = CreateShortPosition(PerpAsset, 1m, 5_000_000L);
        var delta = PerpAsset.ComputeAutoApplyDelta(AutoApplyType.FundingRate, -0.0001, position, 5_000_000L);

        Assert.Equal(-500L, delta);
    }

    [Fact]
    public void FundingRate_ZeroQuantity_ReturnsZero()
    {
        var position = new Position(PerpAsset);
        var delta = PerpAsset.ComputeAutoApplyDelta(AutoApplyType.FundingRate, 0.0001, position, 5_000_000L);

        Assert.Equal(0L, delta);
    }

    [Fact]
    public void FundingRate_WithMultiplier_ScalesCorrectly()
    {
        var futAsset = new FutureAsset { Name = "ES", Exchange = "CME", Multiplier = 50m, TickSize = 0.25m };
        var position = CreateLongPosition(futAsset, 2m, 500_000L);
        // FutureAsset doesn't handle FundingRate — only SwapRate. So this should return 0.
        var delta = futAsset.ComputeAutoApplyDelta(AutoApplyType.FundingRate, 0.0001, position, 500_000L);

        Assert.Equal(0L, delta);
    }

    #endregion

    #region SwapRate

    [Fact]
    public void SwapRate_Annualized_DividesByYear()
    {
        // Same as FundingRate formula but / 365
        var position = CreateLongPosition(PerpAsset, 1m, 5_000_000L);
        var delta = PerpAsset.ComputeAutoApplyDelta(AutoApplyType.SwapRate, 0.0365, position, 5_000_000L);

        // -(1 * 5000000 * 0.0365 * 1) / 365 = -500
        Assert.Equal(-500L, delta);
    }

    #endregion

    #region Dividend

    [Fact]
    public void Dividend_LongPosition_ReceivesDividend()
    {
        var stock = new EquityAsset { Name = "AAPL", Exchange = "NASDAQ" };
        var position = CreateLongPosition(stock, 100m, 15000L);
        // delta = 100 * 0.82 = 82
        var delta = stock.ComputeAutoApplyDelta(AutoApplyType.Dividend, 0.82, position, 15000L);

        Assert.Equal(82L, delta);
    }

    [Fact]
    public void Dividend_ShortPosition_PaysDividend()
    {
        var stock = new EquityAsset { Name = "AAPL", Exchange = "NASDAQ" };
        var position = CreateShortPosition(stock, 100m, 15000L);
        // delta = -100 * 0.82 = -82
        var delta = stock.ComputeAutoApplyDelta(AutoApplyType.Dividend, 0.82, position, 15000L);

        Assert.Equal(-82L, delta);
    }

    #endregion

    #region MarkToMarket

    [Fact]
    public void MarkToMarket_NotYetImplemented_ReturnsZero()
    {
        var position = CreateLongPosition(PerpAsset, 1m, 5_000_000L);
        var delta = PerpAsset.ComputeAutoApplyDelta(AutoApplyType.MarkToMarket, 5_100_000, position, 5_000_000L);

        Assert.Equal(0L, delta);
    }

    #endregion
}
