namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

internal readonly record struct IbOrderStatusUpdate(int OrderId, string Status, decimal Filled, decimal Remaining, double AvgFillPrice);

internal readonly record struct IbFill(int OrderId, string ExecId, double Price, decimal Qty, string Side, long TimeUnixSec);

internal readonly record struct IbOpenOrder(int OrderId, string Account, string Symbol, string Side, string OrderType, decimal Quantity, double LmtPrice, double AuxPrice, string Status);
