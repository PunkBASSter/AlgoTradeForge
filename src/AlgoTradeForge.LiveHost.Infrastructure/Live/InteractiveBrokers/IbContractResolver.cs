using System.Collections.Concurrent;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// Caches successful resolutions by configured-contract value equality. Resolution happens once per instrument
// at startup and reqContractDetails is idempotent, so a rare concurrent first-miss double-fetch is acceptable
// (last-writer-wins); we deliberately do not cache faulted tasks.
internal sealed class IbContractResolver(IIbContractDetailsClient client) : IIbContractResolver
{
    private readonly ConcurrentDictionary<IbContract, ResolvedIbContract> _cache = new();

    public async Task<ResolvedIbContract> Resolve(IbContract spec, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(spec, out var cached))
            return cached;

        var resolved = await client.FetchContractDetails(spec, ct);
        _cache[spec] = resolved;
        return resolved;
    }
}
