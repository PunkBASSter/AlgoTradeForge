using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Strategy;

namespace AlgoTradeForge.Infrastructure.History;

/// <summary>
/// Data sources stored at a single fine <c>source</c> interval (crypto spot/perp: 1m).
/// Always loads the source and resamples up — even when a coarser native feed also exists —
/// so a requested timeframe is reproducible from one canonical base regardless of which
/// coarser aggregates happen to be on disk. Does not read the manifest: the source
/// interval is the archive's guaranteed base.
/// </summary>
public sealed class ResampleFromSourceResolver(TimeFrame source) : IHistoryFeedResolver
{
    public Task<FeedResolution> Resolve(Asset asset, TimeFrame requested, CancellationToken ct = default)
    {
        if (requested.Duration < source.Duration)
            throw new ArgumentException(
                $"Requested timeframe ({requested.Code}) is smaller than the source interval ({source.Code}).",
                nameof(requested));

        return Task.FromResult(new FeedResolution(source.Code, Resample: requested.Duration != source.Duration));
    }
}
