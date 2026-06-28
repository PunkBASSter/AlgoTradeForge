namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

internal readonly record struct IbOpenOrder(int OrderId, string Account, string Symbol, string Side, string OrderType, decimal Quantity, double LmtPrice, double AuxPrice, string Status);
