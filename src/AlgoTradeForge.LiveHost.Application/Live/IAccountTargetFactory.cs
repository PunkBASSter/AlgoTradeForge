using AlgoTradeForge.Domain;

namespace AlgoTradeForge.LiveHost.Application.Live;

public interface IAccountTargetFactory
{
    Task<IAccountTarget> Create(string account, Asset executionAsset, CancellationToken ct = default);
}
