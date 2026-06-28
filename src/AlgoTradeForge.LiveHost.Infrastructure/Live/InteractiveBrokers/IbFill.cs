namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

internal readonly record struct IbFill(int OrderId, string ExecId, string Symbol, double Price, decimal Qty, string Side, long TimeUnixSec);
