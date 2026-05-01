using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Engine;

namespace AlgoTradeForge.Application.Abstractions;

public interface IFeedContextBuilder
{
    /// <summary>
    /// Builds a <see cref="BacktestFeedContext"/> from the asset's <c>feeds.json</c>. Side
    /// feeds (excluding analytical sidecars) are eagerly loaded; sidecars are deferred.
    /// </summary>
    /// <param name="primaryFeedName">
    /// Phase 2b — the strategy's primary bar feed-id (e.g. <c>"1m"</c> for time bars,
    /// <c>"EqI_ticks_500000"</c> for an EqI alt-bar primary). When the primary's manifest
    /// entry carries a non-null <c>Sidecar</c>, the builder registers the sidecar feed on
    /// the context with a lazy loader so strategies that ignore it pay zero cost (TRD §9.4).
    /// <c>null</c> disables sidecar binding.
    /// </param>
    BacktestFeedContext? Build(
        string dataRoot,
        Asset asset,
        DateOnly from,
        DateOnly to,
        string? primaryFeedName = null);
}
