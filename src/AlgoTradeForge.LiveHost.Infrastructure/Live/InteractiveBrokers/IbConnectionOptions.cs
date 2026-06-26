namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

internal sealed record IbConnectionOptions(string Host, int Port, int ClientId);
