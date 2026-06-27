namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// One reqHistoricalTicks "TRADES" row (IB time is Unix seconds; size is decimal).
internal readonly record struct IbHistoricalTick(long TimeSec, double Price, decimal Size);
