namespace AlgoTradeForge.HistoryLoader.Binance;

internal readonly record struct KlineRecord(
    long TimestampMs,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume,
    decimal QuoteVolume,
    int TradeCount,
    decimal TakerBuyVolume,
    decimal TakerBuyQuoteVolume);

internal readonly record struct FeedRecord(long TimestampMs, double[] Values);
