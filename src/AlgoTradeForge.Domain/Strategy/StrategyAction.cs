using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Strategy;

public sealed record StrategyAction(
    Asset Asset,
    OrderSide Side,
    OrderType Type,
    decimal Quantity,
    decimal? LimitPrice = null)
{
    public static StrategyAction MarketBuy(Asset asset, decimal quantity) =>
        new(asset, OrderSide.Buy, OrderType.Market, quantity);

    public static StrategyAction MarketSell(Asset asset, decimal quantity) =>
        new(asset, OrderSide.Sell, OrderType.Market, quantity);

    public static StrategyAction LimitBuy(Asset asset, decimal quantity, decimal price) =>
        new(asset, OrderSide.Buy, OrderType.Limit, quantity, price);

    public static StrategyAction LimitSell(Asset asset, decimal quantity, decimal price) =>
        new(asset, OrderSide.Sell, OrderType.Limit, quantity, price);
}
