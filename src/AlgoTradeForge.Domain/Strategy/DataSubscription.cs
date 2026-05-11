namespace AlgoTradeForge.Domain.Strategy;

/// <summary>Strategy-side data subscription.</summary>
public record DataSubscription(Asset Asset, TimeFrame TimeFrame, string FeedKey = "ohlcv", bool IsExportable = false);
