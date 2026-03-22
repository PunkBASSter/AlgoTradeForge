namespace AlgoTradeForge.Domain.Reporting;

public readonly record struct ClosedTrade(long ExitTimestampMs, long RealizedPnl);
