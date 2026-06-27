using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Strategy.Subscriptions;
using AlgoTradeForge.LiveHost.Application.Collection;
using AlgoTradeForge.Storage.Threading;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// Resolves instrument name -> Asset from the collection config, loaded once and cached. The cache is populated
// ONLY after a successful load: a transient store fault (file lock, partial write) leaves it null so the next
// Resolve retries — unlike a Lazy<Task> which would cache the faulted task and wedge the IB plane for the process
// lifetime. Mirrors the faulted-not-cached contract of IbContractResolver.
internal sealed class CollectionIbInstrumentAssetResolver(ICollectionConfigStore store) : IIbInstrumentAssetResolver
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile Dictionary<string, Asset>? _cache;

    public async ValueTask<Asset> Resolve(string instrument, CancellationToken ct = default)
    {
        var dict = _cache ?? await Load(ct).ConfigureAwait(false);
        if (dict.TryGetValue(instrument, out var asset))
            return asset;
        throw new KeyNotFoundException($"Instrument '{instrument}' not found in collection config.");
    }

    private async Task<Dictionary<string, Asset>> Load(CancellationToken ct)
    {
        using var _ = await _gate.LockAsync(ct).ConfigureAwait(false);
        if (_cache is not null) return _cache; // another caller loaded while we waited on the gate
        var stored = await store.Load(ct).ConfigureAwait(false);
        // Assign only after the awaited load succeeds — a throw above leaves _cache null, so the next call retries.
        return _cache = stored.Config.Feeds.ToDictionary(f => f.AssetName, f => f.RequireAsset());
    }
}
