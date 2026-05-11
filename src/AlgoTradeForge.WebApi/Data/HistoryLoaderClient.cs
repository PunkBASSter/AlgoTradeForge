using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AlgoTradeForge.WebApi.Data;

/// <summary>
/// Typed HTTP client over the HistoryLoader WebApi. Deliberately raw — the proxy round-trips
/// payloads byte-identical so no JSON deserialization happens here.
/// </summary>
public sealed class HistoryLoaderClient
{
    private readonly HttpClient _http;

    public HistoryLoaderClient(HttpClient http) => _http = http;

    public Task<HttpResponseMessage> GetAsync(string relativePath, CancellationToken ct) =>
        _http.GetAsync(relativePath, ct);

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
    /// Opens an SSE progress stream with <see cref="HttpCompletionOption.ResponseHeadersRead"/>
    /// so the body streams through the proxy instead of being buffered.
    /// <c>HttpClient.Timeout</c> only covers the header phase here — the long-lived body read
    /// is gated solely by <paramref name="ct"/>.
    /// </summary>
    public async Task<HttpResponseMessage> OpenProgressStreamAsync(
        string jobId, string? lastEventId, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/aggregations/{jobId}/progress");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (!string.IsNullOrEmpty(lastEventId))
            req.Headers.TryAddWithoutValidation("Last-Event-ID", lastEventId);

        return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
    }
}
