using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Application.Repositories;

public interface IAssetRepository
{
    Task<Asset?> GetByNameAsync(string name, CancellationToken ct = default);
    Task<IReadOnlyList<Asset>> GetAllAsync(CancellationToken ct = default);
}
