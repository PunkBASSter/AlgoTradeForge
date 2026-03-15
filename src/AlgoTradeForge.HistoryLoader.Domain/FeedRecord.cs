namespace AlgoTradeForge.HistoryLoader.Domain;

public readonly record struct FeedRecord(long TimestampMs, double[] Values);
