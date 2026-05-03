using System.Net;
using System.Text.Json;
using AlgoTradeForge.WebApi.Data;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;

namespace AlgoTradeForge.WebApi.Endpoints;

/// <summary>
/// Phase 3 — main API proxy for HistoryLoader §5 endpoints, mounted under <c>/api/data/*</c>
/// (TRD §8). Single-source-of-truth for the FE: it never sees the upstream URL.
/// </summary>
/// <remarks>
/// <para>
/// Stage breakdown (this file accumulates across stages):
/// <list type="bullet">
///   <item>S2: catalog GETs + 5-s TTL cache + status / aggregation-options / snapshot passthrough.</item>
///   <item>S3: <c>POST /aggregate</c> + <c>DELETE /feeds/{id}</c> with write-through cache invalidation.</item>
///   <item>S4: <c>GET /aggregations/{jobId}/progress</c> SSE pass-through.</item>
/// </list>
/// </para>
/// <para>
/// All endpoints run upstream calls through the typed <see cref="HistoryLoaderClient"/>;
/// 5xx and connection failures are translated by <see cref="DataProxyProblem"/>. Upstream
/// 4xx (422 / 423 / 409) is forwarded byte-identical because those carry domain-meaningful
/// payloads the FE differentiates.
/// </para>
/// </remarks>
internal static class DataEndpoints
{
    public static IEndpointRouteBuilder MapDataEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/data");

        // ----- Catalog GETs (cached 5 s) ----------------------------------
        g.MapGet("/exchanges",
            (HttpContext ctx, HistoryLoaderClient client, DataProxyCache cache) =>
                ProxyCachedGet(ctx, client, cache, "/api/v1/exchanges", DataProxyCache.KeyAllExchanges));

        g.MapGet("/exchanges/{exchange}/assets",
            (string exchange, HttpContext ctx, HistoryLoaderClient client, DataProxyCache cache) =>
                ProxyCachedGet(ctx, client, cache,
                    $"/api/v1/exchanges/{exchange}/assets",
                    DataProxyCache.KeyAssetsByExchange(exchange)));

        g.MapGet("/assets",
            (HttpContext ctx, HistoryLoaderClient client, DataProxyCache cache) =>
                ProxyCachedGet(ctx, client, cache, "/api/v1/assets", DataProxyCache.KeyAllAssets));

        // ----- Per-feed endpoints (NOT cached: change rapidly) ------------
        g.MapGet("/exchanges/{exchange}/assets/{asset}/feeds/{feedId}/status",
            (string exchange, string asset, string feedId, HttpContext ctx, HistoryLoaderClient client) =>
                ProxyPassthroughGet(ctx, client,
                    $"/api/v1/exchanges/{exchange}/assets/{asset}/feeds/{feedId}/status"));

        g.MapGet("/exchanges/{exchange}/assets/{asset}/feeds/{feedId}/aggregation-options",
            (string exchange, string asset, string feedId, HttpContext ctx, HistoryLoaderClient client) =>
                ProxyPassthroughGet(ctx, client,
                    $"/api/v1/exchanges/{exchange}/assets/{asset}/feeds/{feedId}/aggregation-options"));

        g.MapGet("/aggregations/{jobId}",
            (string jobId, HttpContext ctx, HistoryLoaderClient client) =>
                ProxyPassthroughGet(ctx, client, $"/api/v1/aggregations/{jobId}"));

        // ----- Mutations: write-through cache invalidation ----------------
        g.MapPost("/exchanges/{exchange}/assets/{asset}/aggregate",
            (string exchange, string asset,
             HttpContext ctx, HistoryLoaderClient client, DataProxyCache cache) =>
                PostAggregate(exchange, asset, ctx, client, cache));

        g.MapDelete("/exchanges/{exchange}/assets/{asset}/feeds/{feedId}",
            (string exchange, string asset, string feedId,
             HttpContext ctx, HistoryLoaderClient client, DataProxyCache cache) =>
                DeleteFeed(exchange, asset, feedId, ctx, client, cache));

        // Phase 6 — cancel an in-flight aggregation job. No cache invalidation needed: cancel
        // doesn't write a manifest entry, so the catalog never saw the would-be feed.
        g.MapDelete("/aggregations/{jobId}",
            (string jobId, HttpContext ctx, HistoryLoaderClient client) =>
                CancelAggregation(jobId, ctx, client));

        // ----- SSE pass-through -------------------------------------------
        g.MapGet("/aggregations/{jobId}/progress",
            (string jobId, HttpContext ctx, HistoryLoaderClient client) =>
                ProxySse(jobId, ctx, client));

        return app;
    }

    /// <summary>
    /// Forwards the upstream SSE stream byte-for-byte. <see cref="IHttpResponseBodyFeature.DisableBuffering"/>
    /// is critical: without it Kestrel buffers small writes and individual events sit until
    /// connection close (which never happens on a long-lived stream).
    /// </summary>
    private static async Task ProxySse(string jobId, HttpContext ctx, HistoryLoaderClient client)
    {
        var lastEventIdRaw = ctx.Request.Headers["Last-Event-ID"].ToString();
        var lastEventId = string.IsNullOrEmpty(lastEventIdRaw) ? null : lastEventIdRaw;

        HttpResponseMessage upstream;
        try
        {
            upstream = await client.OpenProgressStreamAsync(jobId, lastEventId, ctx.RequestAborted);
        }
        catch (HttpRequestException ex)
        {
            await DataProxyProblem.Unavailable(ex.Message).ExecuteAsync(ctx);
            return;
        }
        catch (TaskCanceledException ex) when (!ctx.RequestAborted.IsCancellationRequested)
        {
            await DataProxyProblem.Timeout(ex.Message).ExecuteAsync(ctx);
            return;
        }

        try
        {
            // 410 Gone: upstream's job retention expired. Forward the body verbatim — the FE
            // uses it as a signal to clearJob() and stop reconnecting.
            if (upstream.StatusCode == HttpStatusCode.Gone)
            {
                ctx.Response.StatusCode = (int)HttpStatusCode.Gone;
                ctx.Response.ContentType =
                    upstream.Content.Headers.ContentType?.ToString() ?? "application/json";
                await upstream.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
                return;
            }

            // Upstream 5xx during stream open — translate. Past this point we're in body-stream
            // territory and can't switch to ProblemDetails (headers already sent below).
            if ((int)upstream.StatusCode >= 500)
            {
                var detail = await upstream.Content.ReadAsStringAsync(ctx.RequestAborted);
                await DataProxyProblem.UpstreamError((int)upstream.StatusCode, detail).ExecuteAsync(ctx);
                return;
            }

            // Other non-2xx (404 job not found, 422 invalid path) — forward verbatim.
            if (!upstream.IsSuccessStatusCode)
            {
                ctx.Response.StatusCode = (int)upstream.StatusCode;
                ctx.Response.ContentType =
                    upstream.Content.Headers.ContentType?.ToString() ?? "application/json";
                await upstream.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
                return;
            }

            // Happy path: stream upstream → client. Set SSE headers, disable buffering, flush
            // once so client sees Content-Type before the first event lands.
            ctx.Response.StatusCode = 200;
            ctx.Response.Headers[HeaderNames.ContentType] = "text/event-stream";
            ctx.Response.Headers[HeaderNames.CacheControl] = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);

            await using var stream = await upstream.Content.ReadAsStreamAsync(ctx.RequestAborted);
            // Small buffer keeps SSE frames moving promptly (latency over throughput).
            await stream.CopyToAsync(ctx.Response.Body, bufferSize: 4096, ctx.RequestAborted);
        }
        finally
        {
            upstream.Dispose();
        }
    }

    /// <summary>
    /// Forwards <c>POST /aggregate</c>. On 2xx upstream: invalidate catalog cache BEFORE
    /// flushing the response (so any reader arriving microseconds after 202 cache-misses).
    /// Rewrite <c>Location</c> header to a proxy URL so the FE never sees the upstream path.
    /// 4xx (422/423/409/503) bodies forwarded byte-identical — they carry domain-meaningful
    /// payloads the FE differentiates.
    /// </summary>
    private static async Task PostAggregate(
        string exchange, string asset,
        HttpContext ctx, HistoryLoaderClient client, DataProxyCache cache)
    {
        try
        {
            var body = await ctx.Request.ReadFromJsonAsync<JsonElement>(ctx.RequestAborted);
            using var upstream = await client.PostJsonAsync(
                $"/api/v1/exchanges/{exchange}/assets/{asset}/aggregate", body, ctx.RequestAborted);

            if ((int)upstream.StatusCode >= 500)
            {
                var detail = await upstream.Content.ReadAsStringAsync(ctx.RequestAborted);
                await DataProxyProblem.UpstreamError((int)upstream.StatusCode, detail).ExecuteAsync(ctx);
                return;
            }

            // Cache invalidation precedes any response writes so a concurrent reader arriving
            // immediately after 202/204 doesn't hit the stale catalog cache.
            if (upstream.IsSuccessStatusCode)
                await cache.InvalidateAffectedAsync(exchange, asset, ctx.RequestAborted);

            // Forward status + body + the X-Job-Id header verbatim. Rewrite Location to the
            // proxy URL so the FE only ever sees /api/data/* (TRD §8 invariant).
            ctx.Response.StatusCode = (int)upstream.StatusCode;
            ctx.Response.ContentType = upstream.Content.Headers.ContentType?.ToString() ?? "application/json";

            if (upstream.Headers.TryGetValues("X-Job-Id", out var jobIds))
                ctx.Response.Headers["X-Job-Id"] = jobIds.ToArray();

            if (upstream.Headers.Location is not null)
                ctx.Response.Headers.Location = RewriteLocation(upstream.Headers.Location);

            var responseBytes = await upstream.Content.ReadAsByteArrayAsync(ctx.RequestAborted);
            await ctx.Response.Body.WriteAsync(responseBytes, ctx.RequestAborted);
        }
        catch (HttpRequestException ex)
        {
            await DataProxyProblem.Unavailable(ex.Message).ExecuteAsync(ctx);
        }
        catch (TaskCanceledException ex) when (!ctx.RequestAborted.IsCancellationRequested)
        {
            await DataProxyProblem.Timeout(ex.Message).ExecuteAsync(ctx);
        }
    }

    /// <summary>
    /// Forwards <c>DELETE /feeds/{feedId}</c>. On 204: invalidate catalog cache, then flush.
    /// 4xx (404 / 422 / 423 / 403 for non-AltBar kinds) forwarded byte-identical.
    /// </summary>
    private static async Task DeleteFeed(
        string exchange, string asset, string feedId,
        HttpContext ctx, HistoryLoaderClient client, DataProxyCache cache)
    {
        try
        {
            using var upstream = await client.DeleteAsync(
                $"/api/v1/exchanges/{exchange}/assets/{asset}/feeds/{feedId}", ctx.RequestAborted);

            if ((int)upstream.StatusCode >= 500)
            {
                var detail = await upstream.Content.ReadAsStringAsync(ctx.RequestAborted);
                await DataProxyProblem.UpstreamError((int)upstream.StatusCode, detail).ExecuteAsync(ctx);
                return;
            }

            if (upstream.IsSuccessStatusCode)
                await cache.InvalidateAffectedAsync(exchange, asset, ctx.RequestAborted);

            ctx.Response.StatusCode = (int)upstream.StatusCode;
            // 204 has no body; 4xx bodies are JSON ProblemDetails-like — forward both.
            if (upstream.Content.Headers.ContentLength is > 0 || upstream.StatusCode != System.Net.HttpStatusCode.NoContent)
            {
                ctx.Response.ContentType = upstream.Content.Headers.ContentType?.ToString() ?? "application/json";
                var bytes = await upstream.Content.ReadAsByteArrayAsync(ctx.RequestAborted);
                if (bytes.Length > 0)
                    await ctx.Response.Body.WriteAsync(bytes, ctx.RequestAborted);
            }
        }
        catch (HttpRequestException ex)
        {
            await DataProxyProblem.Unavailable(ex.Message).ExecuteAsync(ctx);
        }
        catch (TaskCanceledException ex) when (!ctx.RequestAborted.IsCancellationRequested)
        {
            await DataProxyProblem.Timeout(ex.Message).ExecuteAsync(ctx);
        }
    }

    /// <summary>
    /// Phase 6 — proxies <c>DELETE /aggregations/{jobId}</c>. Forwards 204 / 404 / 409 byte-identical;
    /// 5xx wrapped in ProblemDetails. No cache invalidation: cancel doesn't write a manifest entry.
    /// </summary>
    private static async Task CancelAggregation(string jobId, HttpContext ctx, HistoryLoaderClient client)
    {
        try
        {
            using var upstream = await client.DeleteAsync(
                $"/api/v1/aggregations/{jobId}", ctx.RequestAborted);

            if ((int)upstream.StatusCode >= 500)
            {
                var detail = await upstream.Content.ReadAsStringAsync(ctx.RequestAborted);
                await DataProxyProblem.UpstreamError((int)upstream.StatusCode, detail).ExecuteAsync(ctx);
                return;
            }

            ctx.Response.StatusCode = (int)upstream.StatusCode;
            if (upstream.Content.Headers.ContentLength is > 0 || upstream.StatusCode != HttpStatusCode.NoContent)
            {
                ctx.Response.ContentType = upstream.Content.Headers.ContentType?.ToString() ?? "application/json";
                var bytes = await upstream.Content.ReadAsByteArrayAsync(ctx.RequestAborted);
                if (bytes.Length > 0)
                    await ctx.Response.Body.WriteAsync(bytes, ctx.RequestAborted);
            }
        }
        catch (HttpRequestException ex)
        {
            await DataProxyProblem.Unavailable(ex.Message).ExecuteAsync(ctx);
        }
        catch (TaskCanceledException ex) when (!ctx.RequestAborted.IsCancellationRequested)
        {
            await DataProxyProblem.Timeout(ex.Message).ExecuteAsync(ctx);
        }
    }

    /// <summary>
    /// Rewrites an upstream <c>Location</c> header that points at <c>/api/v1/aggregations/...</c>
    /// to its proxy equivalent <c>/api/data/aggregations/...</c>. Other paths are returned
    /// unchanged (defensive — should never happen in practice).
    /// </summary>
    private static string RewriteLocation(Uri upstreamLocation)
    {
        var path = upstreamLocation.IsAbsoluteUri ? upstreamLocation.AbsolutePath : upstreamLocation.OriginalString;
        const string Prefix = "/api/v1/";
        if (path.StartsWith(Prefix, StringComparison.Ordinal))
            return "/api/data/" + path[Prefix.Length..];
        return path;
    }

    /// <summary>
    /// Cached GET. Returns the cache entry on hit (5-s sliding TTL); on miss, fetches
    /// upstream and stores 2xx responses. 4xx/5xx are returned without caching.
    /// </summary>
    private static async Task ProxyCachedGet(
        HttpContext ctx, HistoryLoaderClient client, DataProxyCache cache,
        string upstreamPath, string cacheKey)
    {
        try
        {
            var entry = await cache.GetOrFetchAsync(
                cacheKey,
                ct => client.GetAsync(upstreamPath, ct),
                ctx.RequestAborted);
            await WriteEntry(ctx, entry);
        }
        catch (HttpRequestException ex)
        {
            await WriteProblem(ctx, DataProxyProblem.Unavailable(ex.Message));
        }
        catch (TaskCanceledException ex) when (!ctx.RequestAborted.IsCancellationRequested)
        {
            // Upstream timeout (HttpClient.Timeout) — distinguish from caller cancellation.
            await WriteProblem(ctx, DataProxyProblem.Timeout(ex.Message));
        }
    }

    /// <summary>
    /// Passthrough GET (no caching). Upstream body is forwarded byte-identical for 2xx; 4xx
    /// upstream is forwarded as-is (FE differentiates 404 / 422 / 423 / 409). 5xx is wrapped
    /// in ProblemDetails per <see cref="DataProxyProblem"/>.
    /// </summary>
    private static async Task ProxyPassthroughGet(
        HttpContext ctx, HistoryLoaderClient client, string upstreamPath)
    {
        try
        {
            using var upstream = await client.GetAsync(upstreamPath, ctx.RequestAborted);
            if ((int)upstream.StatusCode >= 500)
            {
                var detail = await upstream.Content.ReadAsStringAsync(ctx.RequestAborted);
                await WriteProblem(ctx,
                    DataProxyProblem.UpstreamError((int)upstream.StatusCode, detail));
                return;
            }

            var body = await upstream.Content.ReadAsByteArrayAsync(ctx.RequestAborted);
            ctx.Response.StatusCode = (int)upstream.StatusCode;
            ctx.Response.ContentType =
                upstream.Content.Headers.ContentType?.ToString() ?? "application/json";
            await ctx.Response.Body.WriteAsync(body, ctx.RequestAborted);
        }
        catch (HttpRequestException ex)
        {
            await WriteProblem(ctx, DataProxyProblem.Unavailable(ex.Message));
        }
        catch (TaskCanceledException ex) when (!ctx.RequestAborted.IsCancellationRequested)
        {
            await WriteProblem(ctx, DataProxyProblem.Timeout(ex.Message));
        }
    }

    private static async Task WriteEntry(HttpContext ctx, DataProxyCache.CachedEntry entry)
    {
        ctx.Response.StatusCode = entry.StatusCode;
        ctx.Response.ContentType = entry.ContentType;
        await ctx.Response.Body.WriteAsync(entry.Body, ctx.RequestAborted);
    }

    private static Task WriteProblem(HttpContext ctx, IResult problem) =>
        problem.ExecuteAsync(ctx);
}
