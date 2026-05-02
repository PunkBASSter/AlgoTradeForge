namespace AlgoTradeForge.Domain.Strategy;

/// <summary>
/// Strategy-side data subscription. Phase 4 (TRD §9.1) replaces the raw <c>TimeSpan</c>
/// timeframe with the strongly-typed <see cref="TimeFrame"/> so the API can distinguish a
/// bar-interval from an arbitrary duration. Read-side callers reach the underlying
/// <see cref="TimeSpan"/> via <c>TimeFrame.Duration</c>.
/// </summary>
public record DataSubscription(Asset Asset, TimeFrame TimeFrame, string FeedKey = "ohlcv", bool IsExportable = false);