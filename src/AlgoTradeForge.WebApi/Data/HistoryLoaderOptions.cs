namespace AlgoTradeForge.WebApi.Data;

/// <summary>
/// Configuration for the typed <see cref="HistoryLoaderClient"/>. Bound from the
/// <c>"HistoryLoader"</c> section of <c>appsettings.json</c>. Phase 3 (TRD §8) — main API
/// proxies HistoryLoader's §5 endpoints under <c>/api/data/*</c>; the FE never sees the
/// upstream URL.
/// </summary>
public sealed class HistoryLoaderOptions
{
    /// <summary>
    /// Absolute base URL of the HistoryLoader WebApi (e.g. <c>http://localhost:5050</c>).
    /// Trailing slash is stripped on bind so call sites combine via <c>baseUrl + "/api/v1/..."</c>.
    /// </summary>
    public string BaseUrl { get; init; } = "http://localhost:5050";

    /// <summary>
    /// Per-request timeout for non-SSE calls. Catalog GETs / status / aggregation-options /
    /// snapshot / POST aggregate / DELETE feed all share this budget. SSE streams override
    /// to <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> per request because
    /// <see cref="System.Net.Http.HttpClient.Timeout"/> is total-request, not idle.
    /// </summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
}
