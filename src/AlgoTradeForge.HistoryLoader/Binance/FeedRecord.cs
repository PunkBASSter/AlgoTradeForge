namespace AlgoTradeForge.HistoryLoader.Binance;

internal readonly record struct FeedRecord(long TimestampMs, double[] Values);
