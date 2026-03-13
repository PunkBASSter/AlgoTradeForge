namespace AlgoTradeForge.HistoryLoader.Domain;

public readonly record struct KlineRecord(
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
