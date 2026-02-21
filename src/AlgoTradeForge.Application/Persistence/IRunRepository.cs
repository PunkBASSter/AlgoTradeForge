namespace AlgoTradeForge.Application.Persistence;

public interface IRunRepository
{
    Task SaveAsync(BacktestRunRecord record, CancellationToken ct = default);
    Task<BacktestRunRecord?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<PagedResult<BacktestRunRecord>> QueryAsync(BacktestRunQuery query, CancellationToken ct = default);

    Task SaveOptimizationAsync(OptimizationRunRecord record, CancellationToken ct = default);
    Task<OptimizationRunRecord?> GetOptimizationByIdAsync(Guid id, CancellationToken ct = default);
    Task<PagedResult<OptimizationRunRecord>> QueryOptimizationsAsync(OptimizationRunQuery query, CancellationToken ct = default);

    Task<IReadOnlyList<string>> GetDistinctStrategyNamesAsync(CancellationToken ct = default);
}
