using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;

namespace AlgoTradeForge.WebApi.Data;

/// <summary>
/// Short-TTL cache for catalog GETs proxied to HistoryLoader. Stores raw upstream bytes +
/// Content-Type so the proxy round-trips byte-identical. Catalog endpoints only — per-feed
/// status, aggregation options, per-job snapshots are not cached.
/// </summary>
public sealed class DataProxyCache(IDistributedCache cache)
{
    // Absolute (not sliding) TTL — sliding would let constant read pressure mask out-of-band
    // manifest changes (collector appends, completed jobs) indefinitely. Frontend pacing
    // (use-job-stream.ts) waits Ttl + ~500ms before its cache-bypass refetch.
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(2);

    public const string KeyAllExchanges = "data-proxy:exchanges:all";
    public const string KeyAllAssets = "data-proxy:assets:all";
    public static string KeyAssetsByExchange(string exchange) =>
        $"data-proxy:exchanges:{exchange}:assets";

    /// <summary>
    /// Returns the cached entry if present, otherwise calls <paramref name="fetchUpstream"/>
    /// and caches only on a 2xx response (transient 5xx never sticks).
    /// </summary>
    public async Task<CachedEntry> GetOrFetchAsync(
        string cacheKey,
        Func<CancellationToken, Task<HttpResponseMessage>> fetchUpstream,
        CancellationToken ct)
    {
        var bytes = await cache.GetAsync(cacheKey, ct);
        if (bytes is not null)
        {
            var hit = JsonSerializer.Deserialize<CachedEntry>(bytes);
            if (hit is not null) return hit;
        }

        using var upstream = await fetchUpstream(ct);
        var body = await upstream.Content.ReadAsByteArrayAsync(ct);
        var contentType = upstream.Content.Headers.ContentType?.ToString() ?? "application/json";
        var entry = new CachedEntry((int)upstream.StatusCode, contentType, body);

        if (upstream.IsSuccessStatusCode)
        {
            var serialized = JsonSerializer.SerializeToUtf8Bytes(entry);
            await cache.SetAsync(cacheKey, serialized,
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = Ttl }, ct);
        }
        return entry;
    }

    /// <summary>
    /// Drops the catalog keys after an upstream write succeeds, before flushing the response,
    /// so a reader arriving immediately after the 202/204 cache-misses and re-reads upstream.
    /// </summary>
    public async Task InvalidateAffectedAsync(string exchange, string asset, CancellationToken ct)
    {
        _ = asset;  // future: per-asset granularity if catalog payload splits that fine
        await cache.RemoveAsync(KeyAllExchanges, ct);
        await cache.RemoveAsync(KeyAssetsByExchange(exchange), ct);
        await cache.RemoveAsync(KeyAllAssets, ct);
    }

    public sealed record CachedEntry(int StatusCode, string ContentType, byte[] Body);
}
