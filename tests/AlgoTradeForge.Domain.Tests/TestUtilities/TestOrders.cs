using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Tests.TestUtilities;

public static class TestOrders
{
    private static long _orderIdCounter;

    public static Order MarketBuy(Asset asset, decimal quantity) =>
        new()
        {
            Id = ++_orderIdCounter,
            Asset = asset,
            Side = OrderSide.Buy,
            Type = OrderType.Market,
            Quantity = quantity
        };

    public static Order MarketSell(Asset asset, decimal quantity) =>
        new()
        {
            Id = ++_orderIdCounter,
            Asset = asset,
            Side = OrderSide.Sell,
            Type = OrderType.Market,
            Quantity = quantity
        };

    public static Order LimitBuy(Asset asset, decimal quantity, decimal limitPrice) =>
        new()
        {
            Id = ++_orderIdCounter,
            Asset = asset,
            Side = OrderSide.Buy,
            Type = OrderType.Limit,
            Quantity = quantity,
            LimitPrice = limitPrice
        };

    public static Order LimitSell(Asset asset, decimal quantity, decimal limitPrice) =>
        new()
        {
            Id = ++_orderIdCounter,
            Asset = asset,
            Side = OrderSide.Sell,
            Type = OrderType.Limit,
            Quantity = quantity,
            LimitPrice = limitPrice
        };
}
