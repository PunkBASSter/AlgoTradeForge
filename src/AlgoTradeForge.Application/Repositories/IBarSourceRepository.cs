using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Application.Repositories;

public interface IBarSourceRepository
{
    Task<IBarSource?> GetByNameAsync(string sourceName, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetAvailableSourceNamesAsync(CancellationToken ct = default);
}
