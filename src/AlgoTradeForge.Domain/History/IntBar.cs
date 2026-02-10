namespace AlgoTradeForge.Domain.History;

public readonly record struct IntBar(
    long Open,
    long High,
    long Low,
    long Close,
    long Volume);