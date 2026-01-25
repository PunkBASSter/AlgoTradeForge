using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Strategy;

public sealed record StrategyAction(
    OrderSide Side,
    OrderType Type,
    decimal Quantity,
    decimal? LimitPrice = null)
{
    public static StrategyAction MarketBuy(decimal quantity) =>
        new(OrderSide.Buy, OrderType.Market, quantity);

    public static StrategyAction MarketSell(decimal quantity) =>
        new(OrderSide.Sell, OrderType.Market, quantity);

    public static StrategyAction LimitBuy(decimal quantity, decimal price) =>
        new(OrderSide.Buy, OrderType.Limit, quantity, price);

    public static StrategyAction LimitSell(decimal quantity, decimal price) =>
        new(OrderSide.Sell, OrderType.Limit, quantity, price);
}
