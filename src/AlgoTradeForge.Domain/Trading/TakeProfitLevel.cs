namespace AlgoTradeForge.Domain.Trading;

public readonly record struct TakeProfitLevel(decimal Price, decimal ClosurePercentage);
