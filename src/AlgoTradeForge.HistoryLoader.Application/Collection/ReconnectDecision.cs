namespace AlgoTradeForge.HistoryLoader.Application.Collection;

public readonly record struct ReconnectDecision(int Attempt, bool GiveUp, TimeSpan Delay);
