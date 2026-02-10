namespace AlgoTradeForge.Domain.History;

public readonly record struct RawCandle(
    DateTimeOffset Timestamp,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume);
