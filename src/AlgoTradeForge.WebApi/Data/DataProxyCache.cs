using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;

namespace AlgoTradeForge.WebApi.Data;

/// <summary>
/// 5-second sliding TTL cache (TRD §8) for catalog GETs proxied to HistoryLoader. Stores
/// upstream response body BYTES + Content-Type so the proxy round-trips byte-identical
/// (P3-9 contract). Catalog endpoints only — per-feed status / aggregation-options /
/// per-job snapshot are NOT cached because they change rapidly.
/// </summary>
/// <remarks>
/// <para>
/// Invalidation is write-through: <see cref="InvalidateAffectedAsync"/> is called from the
/// POST/DELETE proxy after upstream success but before the response flushes (S3). Main API
/// can't subscribe to HistoryLoader's in-process <c>ManifestChanged</c> event across the
/// process boundary, so the 5-s TTL is the safety net for state changes the proxy can't see
/// (collector-driven appends from the HistoryLoader's BackgroundServices).
/// </para>
/// <para>
/// Cache value envelope is a small JSON tuple <c>{contentType, body}</c> where <c>body</c>
/// is base64-encoded raw bytes from upstream. We pay one round-trip through JSON for the
/// envelope but the inner body stays opaque — no Content-Type rewrite, no JSON re-serialization,
/// no naming-policy drift.
/// </para>
/// </remarks>
public sealed class DataProxyCache(IDistributedCache cache)
{
    // Absolute (not sliding) TTL: a steady stream of catalog reads otherwise refreshes the
    // entry indefinitely, masking out-of-band manifest changes (collector appends, completed
    // aggregation jobs) for far longer than 5 s. With absolute, every entry dies on schedule
    // regardless of read pressure — worst-case staleness is bounded by Ttl.
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(2);

    /// <summary>Keys held by the catalog cache. Used by <see cref="InvalidateAffectedAsync"/>.</summary>
    public const string KeyAllExchanges = "data-proxy:exchanges:all";
    public const string KeyAllAssets = "data-proxy:assets:all";
    public static string KeyAssetsByExchange(string exchange) =>
        $"data-proxy:exchanges:{exchange}:assets";

    /// <summary>
    /// Get-or-fetch: returns the cached entry if present, otherwise calls
    /// <paramref name="fetchUpstream"/> and stores the result on a 2xx response. Non-2xx
    /// responses are returned to the caller WITHOUT caching (so a transient 5xx doesn't
    /// stick in cache for 5 s).
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
    /// Removes the three catalog cache keys after a POST aggregate / DELETE feed has succeeded
    /// upstream. Called BEFORE writing the response so a reader that arrives milliseconds after
    /// 202/204 cache-misses and re-reads from HistoryLoader.
    /// </summary>
    public async Task InvalidateAffectedAsync(string exchange, string asset, CancellationToken ct)
    {
        _ = asset;  // future: per-asset granularity if catalog payload split that fine
        await cache.RemoveAsync(KeyAllExchanges, ct);
        await cache.RemoveAsync(KeyAssetsByExchange(exchange), ct);
        await cache.RemoveAsync(KeyAllAssets, ct);
    }

    /// <summary>
    /// Cached representation of an upstream response: status + content-type + body bytes.
    /// Body is the raw upstream payload; the proxy writes it back unmodified (P3-9).
    /// </summary>
    /// <param name="StatusCode">HTTP status (always 2xx for cached entries).</param>
    /// <param name="ContentType">Verbatim from upstream.</param>
    /// <param name="Body">Raw upstream bytes; replayed without modification.</param>
    public sealed record CachedEntry(int StatusCode, string ContentType, byte[] Body);
}
