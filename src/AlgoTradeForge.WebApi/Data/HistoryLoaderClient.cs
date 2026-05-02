using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AlgoTradeForge.WebApi.Data;

/// <summary>
/// Typed HTTP client over the HistoryLoader WebApi (TRD §8 main-API-proxy). Deliberately a
/// thin shell — the Phase 3 proxy operates on raw <see cref="HttpResponseMessage"/> /
/// <see cref="System.IO.Stream"/> so payloads round-trip byte-identical (P3-9 requirement).
/// No JSON deserialization happens here.
/// </summary>
/// <remarks>
/// <para>
/// Registered as a typed client (<see cref="IHttpClientFactory"/>); the underlying
/// <c>HttpClient</c> has its <c>BaseAddress</c> set to <see cref="HistoryLoaderOptions.BaseUrl"/>
/// and its <c>Timeout</c> set to <see cref="HistoryLoaderOptions.RequestTimeout"/> on
/// registration. SSE call sites override timeout per-request because <c>HttpClient.Timeout</c>
/// is total-request, not idle.
/// </para>
/// <para>
/// All public methods accept a relative path starting with <c>"/"</c> (e.g.
/// <c>"/api/v1/exchanges"</c>) — the registered <c>BaseAddress</c> handles concatenation.
/// </para>
/// </remarks>
public sealed class HistoryLoaderClient
{
    private readonly HttpClient _http;

    public HistoryLoaderClient(HttpClient http) => _http = http;

    /// <summary>Standard catalog/status GET. Default <c>HttpCompletionOption.ResponseContentRead</c>.</summary>
    public Task<HttpResponseMessage> GetAsync(string relativePath, CancellationToken ct) =>
        _http.GetAsync(relativePath, ct);

    /// <summary>POST with raw JSON body. Used for <c>POST /aggregate</c>; response is forwarded as-is.</summary>
    public async Task<HttpResponseMessage> PostJsonAsync(string relativePath, JsonElement body, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(ms))
            body.WriteTo(writer);
        var content = new ByteArrayContent(ms.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        var req = new HttpRequestMessage(HttpMethod.Post, relativePath) { Content = content };
        return await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
    }

    public Task<HttpResponseMessage> DeleteAsync(string relativePath, CancellationToken ct) =>
        _http.DeleteAsync(relativePath, ct);

    /// <summary>
    /// Opens an SSE progress stream. <see cref="HttpCompletionOption.ResponseHeadersRead"/>
    /// returns as soon as headers arrive (not when the body completes) — without it, the
    /// proxy would buffer the entire upstream stream before flushing.
    /// </summary>
    /// <param name="lastEventId">Forwarded as the <c>Last-Event-ID</c> header for resume (TRD §5.4).</param>
    /// <remarks>
    /// <c>HttpClient.Timeout</c> covers <c>SendAsync</c>, which returns when response headers
    /// arrive (because of <c>ResponseHeadersRead</c>). Subsequent body-stream reads are NOT
    /// gated by it, so the long-lived SSE stream isn't torn at the configured non-SSE budget.
    /// Caller cancellation flows through <paramref name="ct"/>.
    /// </remarks>
    public async Task<HttpResponseMessage> OpenProgressStreamAsync(
        string jobId, string? lastEventId, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/aggregations/{jobId}/progress");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (!string.IsNullOrEmpty(lastEventId))
            req.Headers.TryAddWithoutValidation("Last-Event-ID", lastEventId);

        // ResponseHeadersRead so we can stream the body to the caller as it arrives.
        return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
    }
}
