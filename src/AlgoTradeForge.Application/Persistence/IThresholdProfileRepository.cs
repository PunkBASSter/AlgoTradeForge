namespace AlgoTradeForge.Application.Persistence;

/// <summary>Repository for custom threshold profiles persisted in SQLite.</summary>
public interface IThresholdProfileRepository
{
    Task<IReadOnlyList<ThresholdProfileRecord>> ListAsync(CancellationToken ct = default);
    Task<ThresholdProfileRecord?> GetByNameAsync(string name, CancellationToken ct = default);
    Task SaveAsync(ThresholdProfileRecord record, CancellationToken ct = default);
    Task<bool> DeleteAsync(string name, CancellationToken ct = default);
}

public sealed record ThresholdProfileRecord
{
    public required string Name { get; init; }
    public required string ProfileJson { get; init; }
    public required bool IsBuiltIn { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}
