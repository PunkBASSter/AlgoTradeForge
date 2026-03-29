namespace AlgoTradeForge.Application.Persistence;

public interface IValidationRepository
{
    Task InsertPlaceholderAsync(ValidationRunRecord record, CancellationToken ct = default);
    Task SaveAsync(ValidationRunRecord record, CancellationToken ct = default);
    Task<ValidationRunRecord?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<int> CountByOptimizationIdAsync(Guid optimizationId, CancellationToken ct = default);
    Task<IReadOnlyList<ValidationRunRecord>> ListAsync(CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);
}
