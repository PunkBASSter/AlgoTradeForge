using AlgoTradeForge.Domain;

namespace AlgoTradeForge.LiveHost.Application.Live;

public interface IAccountFundsSource
{
    Task<long> GetFreeFundsScaled(Asset asset, CancellationToken ct = default);
}
