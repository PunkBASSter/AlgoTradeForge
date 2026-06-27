namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// Raw tick-by-tick "AllLast" values straight off the EReader pump (IB time is Unix seconds; size is decimal).
internal readonly record struct IbTradeUpdate(long TimeSec, double Price, decimal Size);
