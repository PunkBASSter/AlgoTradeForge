namespace AlgoTradeForge.Domain.Strategy.Modules.TradeRegistry;

public enum ExpectedOrderType { StopLoss, TakeProfit, Liquidation }

public sealed record ExpectedOrder(
    long OrderId, long GroupId,
    ExpectedOrderType Type, long Price, decimal Quantity);
