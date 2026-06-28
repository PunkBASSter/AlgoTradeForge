using AlgoTradeForge.Domain;

namespace AlgoTradeForge.LiveHost.Application.Live;

public interface IOrderRouter : IAsyncDisposable
{
    Task<IAccountTarget> ResolveTarget(string account, Asset executionAsset, CancellationToken ct = default);
    Task ReleaseTarget(string account, CancellationToken ct = default);
    void TrackOrder(long exchangeOrderId, Guid sessionId);
    void UntrackOrder(long exchangeOrderId);
    bool TryResolveSession(long exchangeOrderId, out Guid sessionId);
    IReadOnlyCollection<IAccountTarget> Targets { get; }
}
