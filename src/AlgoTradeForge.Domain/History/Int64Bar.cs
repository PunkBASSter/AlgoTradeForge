namespace AlgoTradeForge.Domain.History;

public readonly record struct Int64Bar(
    long Open,
    long High,
    long Low,
    long Close,
    long Volume);