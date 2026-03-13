namespace AlgoTradeForge.Domain.Strategy;

public record DataSubscription(Asset Asset, TimeSpan TimeFrame, string FeedKey = "ohlcv", bool IsExportable = false);