namespace AlgoTradeForge.Application.Persistence;

public interface IValidationRepository
{
    Task InsertPlaceholderAsync(ValidationRunRecord record, CancellationToken ct = default);
    Task SaveAsync(ValidationRunRecord record, CancellationToken ct = default);
    Task<ValidationRunRecord?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<int> CountByOptimizationIdAsync(Guid optimizationId, CancellationToken ct = default);
    Task<IReadOnlyList<ValidationRunRecord>> ListAsync(CancellationToken ct = default);
    Task<PagedResult<ValidationRunRecord>> QueryAsync(ValidationRunQuery query, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);

    // Validation group operations
    Task InsertValidationGroupAsync(ValidationGroupRecord record, CancellationToken ct = default);
    Task<ValidationGroupRecord?> GetValidationGroupByIdAsync(Guid id, CancellationToken ct = default);
    Task<PagedResult<ValidationGroupRecord>> QueryValidationGroupsAsync(
        ValidationGroupQuery query, CancellationToken ct = default);
    Task UpdateValidationGroupStatusAsync(
        Guid groupId, string status, DateTimeOffset? completedAt, CancellationToken ct = default);
    Task<bool> DeleteValidationGroupAsync(Guid id, CancellationToken ct = default);
}
