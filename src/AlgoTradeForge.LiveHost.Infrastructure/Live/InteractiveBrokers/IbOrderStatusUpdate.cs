namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

internal readonly record struct IbOrderStatusUpdate(int OrderId, string Status, decimal Filled, decimal Remaining, double AvgFillPrice);
