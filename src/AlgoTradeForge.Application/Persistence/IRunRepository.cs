namespace AlgoTradeForge.Application.Persistence;

public interface IRunRepository
{
    Task SaveAsync(BacktestRunRecord record, CancellationToken ct = default);
    Task<BacktestRunRecord?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<PagedResult<BacktestRunRecord>> QueryAsync(BacktestRunQuery query, CancellationToken ct = default);

    Task InsertOptimizationPlaceholderAsync(OptimizationRunRecord record, CancellationToken ct = default);
    Task SaveOptimizationAsync(OptimizationRunRecord record, CancellationToken ct = default);
    Task<OptimizationRunRecord?> GetOptimizationByIdAsync(Guid id, CancellationToken ct = default);
    Task<OptimizationRunRecord?> GetOptimizationByIdAsync(Guid id, bool includeEquityCurves, CancellationToken ct = default);
    Task<OptimizationRunRecord?> GetOptimizationByIdAsync(Guid id, bool includeEquityCurves, bool includeTrials, CancellationToken ct = default);
    Task<PagedResult<OptimizationRunRecord>> QueryOptimizationsAsync(OptimizationRunQuery query, CancellationToken ct = default);
    Task<PagedResult<BacktestRunRecord>> GetOptimizationTrialsAsync(
        Guid optimizationId, int limit = 50, int offset = 0,
        string? sortBy = null, CancellationToken ct = default);
    Task<bool> DeleteOptimizationAsync(Guid id, CancellationToken ct = default);

    Task<bool> DeleteBacktestAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<TradePoint>?> GetTradePnlAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetDistinctStrategyNamesAsync(CancellationToken ct = default);

    // Optimization group operations
    Task InsertOptimizationGroupAsync(OptimizationGroupRecord record, CancellationToken ct = default);
    Task<OptimizationGroupRecord?> GetOptimizationGroupByIdAsync(Guid id, CancellationToken ct = default);
    Task<PagedResult<OptimizationGroupRecord>> QueryOptimizationGroupsAsync(
        OptimizationGroupQuery query, CancellationToken ct = default);
    Task UpdateOptimizationRunStatusAsync(
        Guid runId, string status, CancellationToken ct = default);
    Task UpdateOptimizationGroupStatusAsync(
        Guid groupId, string status, DateTimeOffset? completedAt, CancellationToken ct = default);
    Task<bool> DeleteOptimizationGroupAsync(Guid id, CancellationToken ct = default);
    Task<PagedResult<BacktestRunRecord>> GetOptimizationGroupTrialsAsync(
        Guid groupId, int limit = 1000, int offset = 0,
        string? sortBy = null, CancellationToken ct = default);
}
