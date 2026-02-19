namespace AlgoTradeForge.Domain.Trading;

public readonly record struct TakeProfitLevel(long Price, decimal ClosurePercentage);
