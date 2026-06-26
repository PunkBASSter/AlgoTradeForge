namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

internal sealed record IbContractDetailsResult(int ConId, string LocalSymbol, string LastTradeDate);
