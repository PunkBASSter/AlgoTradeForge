namespace AlgoTradeForge.Domain.Strategy.Modules.TradeRegistry;

public enum ExpectedOrderType { StopLoss, TakeProfit }

public sealed record ExpectedOrder(
    long OrderId, long GroupId,
    ExpectedOrderType Type, long Price, decimal Quantity);
