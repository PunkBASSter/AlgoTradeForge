using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Tests.TestUtilities;

public static class TestFills
{
    private static readonly DateTimeOffset DefaultTimestamp = new(2024, 1, 1, 9, 30, 0, TimeSpan.Zero);
    private static long _orderIdCounter;

    public static Fill Buy(
        Asset asset,
        long price,
        decimal quantity,
        long commission = 0L,
        DateTimeOffset? timestamp = null) =>
        new(++_orderIdCounter, asset, timestamp ?? DefaultTimestamp, price, quantity, OrderSide.Buy, commission);

    public static Fill Sell(
        Asset asset,
        long price,
        decimal quantity,
        long commission = 0L,
        DateTimeOffset? timestamp = null) =>
        new(++_orderIdCounter, asset, timestamp ?? DefaultTimestamp, price, quantity, OrderSide.Sell, commission);

    public static Fill BuyAapl(long price, decimal quantity, long commission = 0L) =>
        Buy(TestAssets.Aapl, price, quantity, commission);

    public static Fill SellAapl(long price, decimal quantity, long commission = 0L) =>
        Sell(TestAssets.Aapl, price, quantity, commission);

    public static Fill BuyEs(long price, decimal quantity, long commission = 0L) =>
        Buy(TestAssets.EsMini, price, quantity, commission);

    public static Fill SellEs(long price, decimal quantity, long commission = 0L) =>
        Sell(TestAssets.EsMini, price, quantity, commission);
}
