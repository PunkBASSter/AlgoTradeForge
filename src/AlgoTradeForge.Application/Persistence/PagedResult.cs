namespace AlgoTradeForge.Application.Persistence;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount);
