using AlgoTradeForge.Domain;

namespace AlgoTradeForge.Application.Repositories;

public interface IAssetRepository
{
    Task<Asset?> GetByNameAsync(string name, string exchange, CancellationToken ct = default);
    Task<IReadOnlyList<Asset>> GetAllAsync(CancellationToken ct = default);
}
