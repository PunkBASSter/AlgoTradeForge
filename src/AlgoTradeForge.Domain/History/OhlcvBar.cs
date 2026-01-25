namespace AlgoTradeForge.Domain.History;

public readonly record struct OhlcvBar(
    DateTimeOffset Timestamp,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume);
