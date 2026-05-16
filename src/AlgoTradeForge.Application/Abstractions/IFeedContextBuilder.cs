using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Engine;

namespace AlgoTradeForge.Application.Abstractions;

public interface IFeedContextBuilder
{
    /// <summary>
    /// Builds a <see cref="BacktestFeedContext"/> from the asset's <c>feeds.json</c>. Side feeds
    /// are eagerly loaded; the primary's sidecar (if any) is registered as a lazy loader so
    /// strategies that ignore it pay zero cost.
    /// </summary>
    Task<BacktestFeedContext?> Build(
        string dataRoot,
        Asset asset,
        DateOnly from,
        DateOnly to,
        string? primaryFeedName = null,
        CancellationToken ct = default);
}
