namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// Raw reqRealTimeBars 5s bar values straight off the pump (IB date is Unix seconds; volume is decimal).
internal readonly record struct IbRealtimeBar(long DateSec, double Open, double High, double Low, double Close, decimal Volume);
