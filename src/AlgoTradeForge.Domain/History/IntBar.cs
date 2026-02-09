namespace AlgoTradeForge.Domain.History;

public readonly record struct IntBar(
    int Open,
    int High,
    int Low,
    int Close,
    long Volume);