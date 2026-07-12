using System.Net;
using System.Text.Json;
using AlgoTradeForge.WebApi.Data;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;

namespace AlgoTradeForge.WebApi.Endpoints;

/// <summary>
/// Main API proxy for HistoryLoader endpoints, mounted under <c>/api/data/*</c>. The FE
/// never sees the upstream URL. 5xx / connection failures are translated to ProblemDetails;
/// upstream 4xx is forwarded byte-identical (FE differentiates 404 / 422 / 423 / 409).
/// </summary>
internal static class DataEndpoints
{
    public static IEndpointRouteBuilder MapDataEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/data");

        // Catalog GETs — short-TTL cached.
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

        // Per-feed endpoints — uncached (change rapidly).
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

        // Mutations — write-through cache invalidation.
        g.MapPost("/refresh",
            async (HttpContext ctx, HistoryLoaderClient client, DataProxyCache cache) =>
            {
                try
                {
                    using var upstream = await client.Post("/api/v1/catalog/refresh", ctx.RequestAborted);
                    if ((int)upstream.StatusCode >= 500)
                    {
                        var detail = await upstream.Content.ReadAsStringAsync(ctx.RequestAborted);
                        await DataProxyProblem.UpstreamError((int)upstream.StatusCode, detail).ExecuteAsync(ctx);
                        return;
                    }
                    await cache.InvalidateAll(ctx.RequestAborted);
                    ctx.Response.StatusCode = (int)upstream.StatusCode;
                    var bytes = await upstream.Content.ReadAsByteArrayAsync(ctx.RequestAborted);
                    if (bytes.Length > 0)
                    {
                        ctx.Response.ContentType = upstream.Content.Headers.ContentType?.ToString() ?? "application/json";
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
            });

        g.MapPost("/exchanges/{exchange}/assets/{asset}/aggregate",
            (string exchange, string asset,
             HttpContext ctx, HistoryLoaderClient client, DataProxyCache cache) =>
                PostAggregate(exchange, asset, ctx, client, cache));

        g.MapDelete("/exchanges/{exchange}/assets/{asset}/feeds/{feedId}",
            (string exchange, string asset, string feedId,
             HttpContext ctx, HistoryLoaderClient client, DataProxyCache cache) =>
                DeleteFeed(exchange, asset, feedId, ctx, client, cache));

        // Cancel in-flight job — no cache invalidation; cancel never wrote a manifest entry.
        g.MapDelete("/aggregations/{jobId}",
            (string jobId, HttpContext ctx, HistoryLoaderClient client) =>
                CancelAggregation(jobId, ctx, client));

        g.MapGet("/aggregations/{jobId}/progress",
            (string jobId, HttpContext ctx, HistoryLoaderClient client) =>
                ProxySse(jobId, ctx, client));

        // Archive coverage + load jobs (phase 2) — uncached pass-throughs; loads poll fast
        // and coverage must reflect a finishing job immediately.
        g.MapGet("/coverage",
            (HttpContext ctx, HistoryLoaderClient client) =>
                ProxyPassthroughGet(ctx, client, $"/api/v1/coverage{ctx.Request.QueryString.Value}"));

        g.MapPost("/loads",
            async (HttpContext ctx, HistoryLoaderClient client) =>
            {
                try
                {
                    var body = await ctx.Request.ReadFromJsonAsync<JsonElement>(ctx.RequestAborted);
                    using var upstream = await client.PostJsonAsync("/api/v1/loads", body, ctx.RequestAborted);
                    if ((int)upstream.StatusCode >= 500)
                    {
                        var detail = await upstream.Content.ReadAsStringAsync(ctx.RequestAborted);
                        await DataProxyProblem.UpstreamError((int)upstream.StatusCode, detail).ExecuteAsync(ctx);
                        return;
                    }
                    ctx.Response.StatusCode = (int)upstream.StatusCode;
                    ctx.Response.ContentType = upstream.Content.Headers.ContentType?.ToString() ?? "application/json";
                    var bytes = await upstream.Content.ReadAsByteArrayAsync(ctx.RequestAborted);
                    await ctx.Response.Body.WriteAsync(bytes, ctx.RequestAborted);
                }
                catch (JsonException)
                {
                    // Malformed request body must not surface as a 500 (existing PostAggregate
                    // shares this gap — follow-up, out of scope here).
                    ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await ctx.Response.WriteAsJsonAsync(new { error = "invalid_json" }, ctx.RequestAborted);
                }
                catch (HttpRequestException ex)
                {
                    await DataProxyProblem.Unavailable(ex.Message).ExecuteAsync(ctx);
                }
                catch (TaskCanceledException ex) when (!ctx.RequestAborted.IsCancellationRequested)
                {
                    await DataProxyProblem.Timeout(ex.Message).ExecuteAsync(ctx);
                }
            });

        g.MapGet("/loads/{jobId}",
            (string jobId, HttpContext ctx, HistoryLoaderClient client) =>
                ProxyPassthroughGet(ctx, client, $"/api/v1/loads/{Uri.EscapeDataString(jobId)}"));

        // Groups + desired-state — uncached (live config / reconciler state, not catalog).
        g.MapGet("/groups",
            (HttpContext ctx, HistoryLoaderClient client) =>
                ProxyPassthroughGet(ctx, client, "/api/v1/groups"));

        g.MapGet("/groups/{name}",
            (string name, HttpContext ctx, HistoryLoaderClient client) =>
                ProxyGroupGet(name, ctx, client));

        g.MapPut("/groups/{name}",
            (string name, HttpContext ctx, HistoryLoaderClient client) =>
                ProxyGroupPut(name, ctx, client));

        g.MapDelete("/groups/{name}",
            (string name, HttpContext ctx, HistoryLoaderClient client) =>
                ProxyGroupDelete(name, ctx, client));

        g.MapPost("/groups/validate",
            (HttpContext ctx, HistoryLoaderClient client) =>
                ProxyGroupValidate(ctx, client));

        g.MapGet("/desired-state",
            (HttpContext ctx, HistoryLoaderClient client) =>
                ProxyPassthroughGet(ctx, client, $"/api/v1/desired-state{ctx.Request.QueryString.Value}"));

        // Unified jobs + materialize — uncached (live job state, not catalog data).
        g.MapGet("/jobs",
            (HttpContext ctx, HistoryLoaderClient client) =>
                ProxyPassthroughGet(ctx, client, $"/api/v1/jobs{ctx.Request.QueryString.Value}"));

        g.MapGet("/jobs/{jobId}",
            (string jobId, HttpContext ctx, HistoryLoaderClient client) =>
                ProxyPassthroughGet(ctx, client, $"/api/v1/jobs/{Uri.EscapeDataString(jobId)}"));

        g.MapGet("/jobs/{jobId}/progress",
            (string jobId, HttpContext ctx, HistoryLoaderClient client) =>
                ProxySseJobs(jobId, ctx, client));

        g.MapPost("/materialize",
            async (HttpContext ctx, HistoryLoaderClient client) =>
            {
                try
                {
                    var body = await ctx.Request.ReadFromJsonAsync<JsonElement>(ctx.RequestAborted);
                    using var upstream = await client.PostMaterialize(body, ctx.RequestAborted);
                    if ((int)upstream.StatusCode >= 500)
                    {
                        var detail = await upstream.Content.ReadAsStringAsync(ctx.RequestAborted);
                        await DataProxyProblem.UpstreamError((int)upstream.StatusCode, detail).ExecuteAsync(ctx);
                        return;
                    }
                    ctx.Response.StatusCode = (int)upstream.StatusCode;
                    ctx.Response.ContentType = upstream.Content.Headers.ContentType?.ToString() ?? "application/json";
                    var bytes = await upstream.Content.ReadAsByteArrayAsync(ctx.RequestAborted);
                    await ctx.Response.Body.WriteAsync(bytes, ctx.RequestAborted);
                }
                catch (JsonException)
                {
                    ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await ctx.Response.WriteAsJsonAsync(new { error = "invalid_json" }, ctx.RequestAborted);
                }
                catch (HttpRequestException ex)
                {
                    await DataProxyProblem.Unavailable(ex.Message).ExecuteAsync(ctx);
                }
                catch (TaskCanceledException ex) when (!ctx.RequestAborted.IsCancellationRequested)
                {
                    await DataProxyProblem.Timeout(ex.Message).ExecuteAsync(ctx);
                }
            });

        g.MapDelete("/jobs/{jobId}",
            (string jobId, HttpContext ctx, HistoryLoaderClient client) =>
                CancelJob(jobId, ctx, client));

        return app;
    }

    /// <summary>
    /// Forwards the upstream SSE stream for an aggregation job byte-for-byte.
    /// <c>DisableBuffering</c> is required — Kestrel otherwise buffers small writes and
    /// individual events sit until connection close.
    /// </summary>
    private static Task ProxySse(string jobId, HttpContext ctx, HistoryLoaderClient client)
    {
        var lastEventId = GetLastEventId(ctx);
        return ForwardSseAsync(ctx, ct => client.OpenProgressStreamAsync(jobId, lastEventId, ct));
    }

    /// <summary>Forwards the upstream SSE stream for a unified job (<c>/api/v1/jobs/{id}/progress</c>).</summary>
    private static Task ProxySseJobs(string jobId, HttpContext ctx, HistoryLoaderClient client)
    {
        var lastEventId = GetLastEventId(ctx);
        return ForwardSseAsync(ctx, ct => client.OpenJobProgressStreamAsync(jobId, lastEventId, ct));
    }

    private static string? GetLastEventId(HttpContext ctx)
    {
        var raw = ctx.Request.Headers["Last-Event-ID"].ToString();
        return string.IsNullOrEmpty(raw) ? null : raw;
    }

    /// <summary>
    /// Shared SSE forwarder. Opens the stream via <paramref name="openStream"/>, handles error
    /// status codes, then pipes the response body byte-for-byte with buffering disabled.
    /// </summary>
    private static async Task ForwardSseAsync(
        HttpContext ctx, Func<CancellationToken, Task<HttpResponseMessage>> openStream)
    {
        HttpResponseMessage upstream;
        try
        {
            upstream = await openStream(ctx.RequestAborted);
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
            // 410 Gone — upstream retention expired; FE uses it to clearJob() and stop reconnecting.
            if (upstream.StatusCode == HttpStatusCode.Gone)
            {
                ctx.Response.StatusCode = (int)HttpStatusCode.Gone;
                ctx.Response.ContentType =
                    upstream.Content.Headers.ContentType?.ToString() ?? "application/json";
                await upstream.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
                return;
            }

            // 5xx must be translated before any body-stream headers are sent.
            if ((int)upstream.StatusCode >= 500)
            {
                var detail = await upstream.Content.ReadAsStringAsync(ctx.RequestAborted);
                await DataProxyProblem.UpstreamError((int)upstream.StatusCode, detail).ExecuteAsync(ctx);
                return;
            }

            // Other non-2xx (404, 422) — forward verbatim.
            if (!upstream.IsSuccessStatusCode)
            {
                ctx.Response.StatusCode = (int)upstream.StatusCode;
                ctx.Response.ContentType =
                    upstream.Content.Headers.ContentType?.ToString() ?? "application/json";
                await upstream.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
                return;
            }

            // Stream upstream → client. Flush once so the client sees Content-Type
            // before the first event arrives.
            ctx.Response.StatusCode = 200;
            ctx.Response.Headers[HeaderNames.ContentType] = "text/event-stream";
            ctx.Response.Headers[HeaderNames.CacheControl] = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);

            await using var stream = await upstream.Content.ReadAsStreamAsync(ctx.RequestAborted);
            // Small buffer favors SSE latency over throughput.
            await stream.CopyToAsync(ctx.Response.Body, bufferSize: 4096, ctx.RequestAborted);
        }
        finally
        {
            upstream.Dispose();
        }
    }

    /// <summary>
    /// Forwards <c>POST /aggregate</c>. On 2xx upstream: invalidate catalog cache before
    /// flushing the response, and rewrite the Location header to the proxy URL.
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

            // Invalidate before writing the response so a reader arriving right after 202
            // doesn't hit the stale catalog cache.
            if (upstream.IsSuccessStatusCode)
                await cache.InvalidateAffectedAsync(exchange, asset, ctx.RequestAborted);

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

    /// <summary>Forwards <c>DELETE /feeds/{feedId}</c>; invalidates catalog cache on 204.</summary>
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

    /// <summary>Proxies <c>DELETE /aggregations/{jobId}</c>; no cache invalidation.</summary>
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

    /// <summary>Proxies <c>DELETE /jobs/{jobId}</c>; no cache invalidation.</summary>
    private static async Task CancelJob(string jobId, HttpContext ctx, HistoryLoaderClient client)
    {
        try
        {
            using var upstream = await client.DeleteJob(jobId, ctx.RequestAborted);
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
    /// GET /groups/{name} — forwards body + ETag response header (for optimistic-concurrency reads).
    /// </summary>
    private static async Task ProxyGroupGet(string name, HttpContext ctx, HistoryLoaderClient client)
    {
        try
        {
            using var upstream = await client.GetAsync(
                $"/api/v1/groups/{Uri.EscapeDataString(name)}", ctx.RequestAborted);
            if ((int)upstream.StatusCode >= 500)
            {
                var detail = await upstream.Content.ReadAsStringAsync(ctx.RequestAborted);
                await WriteProblem(ctx, DataProxyProblem.UpstreamError((int)upstream.StatusCode, detail));
                return;
            }
            if (upstream.Headers.TryGetValues("ETag", out var etags))
                ctx.Response.Headers["ETag"] = etags.ToArray();
            var body = await upstream.Content.ReadAsByteArrayAsync(ctx.RequestAborted);
            ctx.Response.StatusCode = (int)upstream.StatusCode;
            ctx.Response.ContentType = upstream.Content.Headers.ContentType?.ToString() ?? "application/json";
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

    /// <summary>
    /// PUT /groups/{name} — forwards If-Match request header and ETag response header for
    /// conditional-update (optimistic concurrency). JsonException → 400.
    /// </summary>
    private static async Task ProxyGroupPut(string name, HttpContext ctx, HistoryLoaderClient client)
    {
        try
        {
            var body = await ctx.Request.ReadFromJsonAsync<JsonElement>(ctx.RequestAborted);
            var ifMatch = ctx.Request.Headers["If-Match"].FirstOrDefault();
            using var upstream = await client.PutJson(
                $"/api/v1/groups/{Uri.EscapeDataString(name)}", body, ifMatch, ctx.RequestAborted);
            if ((int)upstream.StatusCode >= 500)
            {
                var detail = await upstream.Content.ReadAsStringAsync(ctx.RequestAborted);
                await WriteProblem(ctx, DataProxyProblem.UpstreamError((int)upstream.StatusCode, detail));
                return;
            }
            if (upstream.Headers.TryGetValues("ETag", out var etags))
                ctx.Response.Headers["ETag"] = etags.ToArray();
            ctx.Response.StatusCode = (int)upstream.StatusCode;
            ctx.Response.ContentType = upstream.Content.Headers.ContentType?.ToString() ?? "application/json";
            var bytes = await upstream.Content.ReadAsByteArrayAsync(ctx.RequestAborted);
            await ctx.Response.Body.WriteAsync(bytes, ctx.RequestAborted);
        }
        catch (JsonException)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsJsonAsync(new { error = "invalid_json" }, ctx.RequestAborted);
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

    /// <summary>DELETE /groups/{name} — forwards ETag response header when upstream returns one.</summary>
    private static async Task ProxyGroupDelete(string name, HttpContext ctx, HistoryLoaderClient client)
    {
        try
        {
            using var upstream = await client.DeleteAsync(
                $"/api/v1/groups/{Uri.EscapeDataString(name)}", ctx.RequestAborted);
            if ((int)upstream.StatusCode >= 500)
            {
                var detail = await upstream.Content.ReadAsStringAsync(ctx.RequestAborted);
                await WriteProblem(ctx, DataProxyProblem.UpstreamError((int)upstream.StatusCode, detail));
                return;
            }
            if (upstream.Headers.TryGetValues("ETag", out var etags))
                ctx.Response.Headers["ETag"] = etags.ToArray();
            ctx.Response.StatusCode = (int)upstream.StatusCode;
            if (upstream.Content.Headers.ContentLength is > 0 ||
                upstream.StatusCode != HttpStatusCode.NoContent)
            {
                ctx.Response.ContentType = upstream.Content.Headers.ContentType?.ToString() ?? "application/json";
                var bytes = await upstream.Content.ReadAsByteArrayAsync(ctx.RequestAborted);
                if (bytes.Length > 0)
                    await ctx.Response.Body.WriteAsync(bytes, ctx.RequestAborted);
            }
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

    /// <summary>POST /groups/validate — forwards request body verbatim. JsonException → 400.</summary>
    private static async Task ProxyGroupValidate(HttpContext ctx, HistoryLoaderClient client)
    {
        try
        {
            var body = await ctx.Request.ReadFromJsonAsync<JsonElement>(ctx.RequestAborted);
            using var upstream = await client.PostJsonAsync("/api/v1/groups/validate", body, ctx.RequestAborted);
            if ((int)upstream.StatusCode >= 500)
            {
                var detail = await upstream.Content.ReadAsStringAsync(ctx.RequestAborted);
                await WriteProblem(ctx, DataProxyProblem.UpstreamError((int)upstream.StatusCode, detail));
                return;
            }
            ctx.Response.StatusCode = (int)upstream.StatusCode;
            ctx.Response.ContentType = upstream.Content.Headers.ContentType?.ToString() ?? "application/json";
            var bytes = await upstream.Content.ReadAsByteArrayAsync(ctx.RequestAborted);
            await ctx.Response.Body.WriteAsync(bytes, ctx.RequestAborted);
        }
        catch (JsonException)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsJsonAsync(new { error = "invalid_json" }, ctx.RequestAborted);
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

    /// <summary>Rewrites <c>/api/v1/...</c> Location headers to <c>/api/data/...</c>.</summary>
    private static string RewriteLocation(Uri upstreamLocation)
    {
        var path = upstreamLocation.IsAbsoluteUri ? upstreamLocation.AbsolutePath : upstreamLocation.OriginalString;
        const string Prefix = "/api/v1/";
        if (path.StartsWith(Prefix, StringComparison.Ordinal))
            return "/api/data/" + path[Prefix.Length..];
        return path;
    }

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
