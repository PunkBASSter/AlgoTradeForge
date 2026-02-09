using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Tests.TestUtilities;

public static class TestFills
{
    private static readonly DateTimeOffset DefaultTimestamp = new(2024, 1, 1, 9, 30, 0, TimeSpan.Zero);
    private static long _orderIdCounter;

    public static Fill Buy(
        Asset asset,
        decimal price,
        decimal quantity,
        decimal commission = 0m,
        DateTimeOffset? timestamp = null) =>
        new(++_orderIdCounter, asset, timestamp ?? DefaultTimestamp, price, quantity, OrderSide.Buy, commission);

    public static Fill Sell(
        Asset asset,
        decimal price,
        decimal quantity,
        decimal commission = 0m,
        DateTimeOffset? timestamp = null) =>
        new(++_orderIdCounter, asset, timestamp ?? DefaultTimestamp, price, quantity, OrderSide.Sell, commission);

    public static Fill BuyAapl(decimal price, decimal quantity, decimal commission = 0m) =>
        Buy(TestAssets.Aapl, price, quantity, commission);

    public static Fill SellAapl(decimal price, decimal quantity, decimal commission = 0m) =>
        Sell(TestAssets.Aapl, price, quantity, commission);

    public static Fill BuyEs(decimal price, decimal quantity, decimal commission = 0m) =>
        Buy(TestAssets.EsMini, price, quantity, commission);

    public static Fill SellEs(decimal price, decimal quantity, decimal commission = 0m) =>
        Sell(TestAssets.EsMini, price, quantity, commission);
}
