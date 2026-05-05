using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AlgoTradeForge.WebApi.Tests.Data;

/// <summary>
/// Backend test gates for Phase 3 main-API proxy (TRD §8). Covers:
/// <list type="bullet">
///   <item>P3-7 — SSE pass-through preserves chunked transfer + invokes
///         <c>IHttpResponseBodyFeature.DisableBuffering()</c>.</item>
///   <item>P3-8 — <c>POST /aggregate</c> + <c>DELETE feed</c> invalidate the catalog cache.</item>
///   <item>P3-9 — Catalog payloads round-trip through the proxy byte-identical (no
///         deserialization / re-serialization).</item>
///   <item>P3-6 — 5xx → ProblemDetails (502 unavailable, 504 timeout, passthrough 5xx).</item>
///   <item>S3 invariants — <c>Location</c> rewrite, <c>X-Job-Id</c> forwarding, DELETE
///         invalidation.</item>
/// </list>
/// Each test instantiates its own factory so canned-response handlers don't race.
/// </summary>
public sealed class DataProxyTests
{
    // -------------------------------------------------------------------------
    // P3-9 — byte-identical round-trip (canonical TRD §5.1 catalog shape)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CatalogPayloads_RoundTripUnchanged_GetExchanges()
    {
        var canonical = """{"exchanges":[{"name":"binance","asset_count":2}]}"""u8.ToArray();
        await using var factory = new DataProxyTestFactory();
        factory.Handler.Respond(_ => JsonResp(canonical));

        using var client = factory.CreateClient();
        var bytes = await client.GetByteArrayAsync("/api/data/exchanges", TestContext.Current.CancellationToken);

        Assert.Equal(canonical, bytes);
    }

    [Fact]
    public async Task CatalogPayloads_RoundTripUnchanged_GetAssets()
    {
        var canonical = """{"assets":[{"exchange":"binance","asset":"BTCUSDT_perp","feeds":[]}]}"""u8.ToArray();
        await using var factory = new DataProxyTestFactory();
        factory.Handler.Respond(_ => JsonResp(canonical));

        using var client = factory.CreateClient();
        var resp = await client.GetAsync("/api/data/assets", TestContext.Current.CancellationToken);
        var bytes = await resp.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);

        Assert.Equal(canonical, bytes);
        Assert.Equal("application/json; charset=utf-8", resp.Content.Headers.ContentType?.ToString());
    }

    [Fact]
    public async Task CatalogPayloads_RoundTripUnchanged_GetExchangeAssets()
    {
        var canonical = """{"exchange":"binance","assets":[{"asset":"BTCUSDT_perp","feeds":[{"id":"1m","kind":"OHLCV_TimeBar"}]}]}"""u8.ToArray();
        await using var factory = new DataProxyTestFactory();
        factory.Handler.Respond(_ => JsonResp(canonical));

        using var client = factory.CreateClient();
        var bytes = await client.GetByteArrayAsync(
            "/api/data/exchanges/binance/assets",
            TestContext.Current.CancellationToken);

        Assert.Equal(canonical, bytes);
    }

    // -------------------------------------------------------------------------
    // P3-8 — write-through cache invalidation
    // -------------------------------------------------------------------------

    [Fact]
    public async Task PostAggregate_InvalidatesCache_ConcurrentReaderSeesFresh()
    {
        await using var factory = new DataProxyTestFactory();
        factory.Handler.RespondAsync(req =>
        {
            return req.Method == HttpMethod.Post
                ? Task.FromResult(JsonResp("""{"job_id":"j1","state":"queued"}"""u8.ToArray(),
                    HttpStatusCode.Accepted, locationHeader: "/api/v1/aggregations/j1/progress",
                    extraHeaders: ("X-Job-Id", "j1")))
                : Task.FromResult(JsonResp("""{"exchanges":[]}"""u8.ToArray()));
        });

        using var client = factory.CreateClient();
        // Pre-warm cache.
        await client.GetByteArrayAsync("/api/data/exchanges", TestContext.Current.CancellationToken);
        // Second read in TTL window — cache hit.
        await client.GetByteArrayAsync("/api/data/exchanges", TestContext.Current.CancellationToken);
        Assert.Equal(1, ExactPathCallCount(factory, "/api/v1/exchanges"));

        // POST aggregate triggers invalidation.
        var resp = await client.PostAsJsonAsync(
            "/api/data/exchanges/binance/assets/BTCUSDT_perp/aggregate",
            new { source_feed_id = "1m", type_code = "EqV" },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        // Next read must miss cache → upstream call count for exact path == 2.
        await client.GetByteArrayAsync("/api/data/exchanges", TestContext.Current.CancellationToken);
        Assert.Equal(2, ExactPathCallCount(factory, "/api/v1/exchanges"));
    }

    // P6-14 — Cancel job proxy (Phase 6).

    [Fact]
    public async Task CancelJob_ForwardsDelete_AndReturns204_OnSuccess()
    {
        await using var factory = new DataProxyTestFactory();
        var observedPath = "";
        factory.Handler.RespondAsync(req =>
        {
            observedPath = req.RequestUri!.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        });

        using var client = factory.CreateClient();
        var resp = await client.DeleteAsync(
            "/api/data/aggregations/abc123",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        Assert.Equal("/api/v1/aggregations/abc123", observedPath);
    }

    [Fact]
    public async Task CancelJob_Forwards404_WhenJobUnknown()
    {
        await using var factory = new DataProxyTestFactory();
        factory.Handler.RespondAsync(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("""{"error":"job_not_found_or_expired","job_id":"missing"}""", Encoding.UTF8, "application/json"),
        }));

        using var client = factory.CreateClient();
        var resp = await client.DeleteAsync(
            "/api/data/aggregations/missing",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("job_not_found_or_expired", body);
    }

    [Fact]
    public async Task CancelJob_Forwards409_WhenJobAlreadyTerminal()
    {
        await using var factory = new DataProxyTestFactory();
        factory.Handler.RespondAsync(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent("""{"code":"job_already_terminal","job_id":"j1","state":"complete"}""", Encoding.UTF8, "application/json"),
        }));

        using var client = factory.CreateClient();
        var resp = await client.DeleteAsync(
            "/api/data/aggregations/j1",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("job_already_terminal", body);
    }

    [Fact]
    public async Task DeleteFeed_InvalidatesCache()
    {
        await using var factory = new DataProxyTestFactory();
        factory.Handler.RespondAsync(req =>
        {
            return req.Method == HttpMethod.Delete
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent))
                : Task.FromResult(JsonResp("""{"exchanges":[]}"""u8.ToArray()));
        });

        using var client = factory.CreateClient();
        await client.GetByteArrayAsync("/api/data/exchanges", TestContext.Current.CancellationToken);
        Assert.Equal(1, ExactPathCallCount(factory, "/api/v1/exchanges"));

        var del = await client.DeleteAsync(
            "/api/data/exchanges/binance/assets/BTCUSDT_perp/feeds/EqV_1m_1000",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        await client.GetByteArrayAsync("/api/data/exchanges", TestContext.Current.CancellationToken);
        Assert.Equal(2, ExactPathCallCount(factory, "/api/v1/exchanges"));
    }

    // -------------------------------------------------------------------------
    // P3-7 — SSE pass-through (chunked + DisableBuffering)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SsePassThrough_PreservesContentType_AndDisablesBuffering()
    {
        // Canonical SSE frames per TRD §5.4: id + event + data + blank line.
        var sseBody = """
id: 1
event: started
data: {"job_id":"j1","feed_id":"EqV_1m_1000"}

id: 2
event: progress
data: {"bars_emitted":42,"current_partition":"2024-01"}


""".ReplaceLineEndings("\n");
        var sseBytes = Encoding.UTF8.GetBytes(sseBody);

        await using var factory = new DataProxyTestFactory();
        factory.Handler.Respond(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(sseBytes),
            };
            resp.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return resp;
        });

        using var client = factory.CreateClient();
        var resp = await client.GetAsync(
            "/api/data/aggregations/j1/progress",
            HttpCompletionOption.ResponseContentRead,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/event-stream", resp.Content.Headers.ContentType?.MediaType);
        Assert.Contains(resp.Headers.GetValues("Cache-Control"), v => v.Contains("no-cache"));
        Assert.Contains(resp.Headers.GetValues("X-Accel-Buffering"), v => v == "no");
        Assert.True(factory.BufferingCapture.DisableBufferingCalled,
            "IHttpResponseBodyFeature.DisableBuffering() must be invoked on the SSE proxy route.");

        var body = await resp.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(sseBytes, body);
    }

    [Fact]
    public async Task SsePassThrough_410Gone_ForwardsStatusAndBody()
    {
        // Job retention expired — upstream responds 410 with a JSON body. FE uses this as
        // a clearJob() signal.
        var body = """{"error":"job_not_found_or_expired","job_id":"old"}"""u8.ToArray();
        await using var factory = new DataProxyTestFactory();
        factory.Handler.Respond(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Gone) { Content = new ByteArrayContent(body) };
            r.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return r;
        });

        using var client = factory.CreateClient();
        var resp = await client.GetAsync(
            "/api/data/aggregations/old/progress",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Gone, resp.StatusCode);
        var bytes = await resp.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(body, bytes);
    }

    // -------------------------------------------------------------------------
    // S3 invariants — Location rewrite + X-Job-Id forwarding
    // -------------------------------------------------------------------------

    [Fact]
    public async Task PostAggregate_RewritesLocationHeader_ToProxyPath()
    {
        await using var factory = new DataProxyTestFactory();
        factory.Handler.Respond(_ => JsonResp(
            """{"job_id":"abc","state":"queued"}"""u8.ToArray(),
            HttpStatusCode.Accepted,
            locationHeader: "/api/v1/aggregations/abc/progress",
            extraHeaders: ("X-Job-Id", "abc")));

        using var client = factory.CreateClient();
        var resp = await client.PostAsJsonAsync(
            "/api/data/exchanges/binance/assets/BTCUSDT_perp/aggregate",
            new { source_feed_id = "1m", type_code = "EqV" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        Assert.Equal("/api/data/aggregations/abc/progress", resp.Headers.Location?.ToString());
        Assert.Equal("abc", resp.Headers.GetValues("X-Job-Id").Single());
    }

    [Fact]
    public async Task PostAggregate_423Locked_BodyForwardedByteIdentical()
    {
        // 423 carries domain-meaningful payload {code,feed_id,existing_job_id,existing_job_state};
        // FE differentiates on these fields. Must NOT be translated to ProblemDetails.
        var body423 = """{"code":"feed_already_locked","feed_id":"EqV_1m_1000","existing_job_id":"j2","existing_job_state":"running"}"""u8.ToArray();
        await using var factory = new DataProxyTestFactory();
        factory.Handler.Respond(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Locked) { Content = new ByteArrayContent(body423) };
            r.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return r;
        });

        using var client = factory.CreateClient();
        var resp = await client.PostAsJsonAsync(
            "/api/data/exchanges/binance/assets/BTCUSDT_perp/aggregate",
            new { source_feed_id = "1m" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Locked, resp.StatusCode);
        var bytes = await resp.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(body423, bytes);
    }

    // -------------------------------------------------------------------------
    // P3-6 — 5xx → ProblemDetails
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetExchanges_502_When_HistoryLoaderUnreachable()
    {
        await using var factory = new DataProxyTestFactory();
        factory.Handler.RespondAsync(_ => throw new HttpRequestException("connection refused"));

        using var client = factory.CreateClient();
        var resp = await client.GetAsync("/api/data/exchanges", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("history_loader_unavailable", doc.GetProperty("code").GetString());
    }

    [Fact]
    public async Task GetExchanges_504_When_UpstreamTimeout()
    {
        await using var factory = new DataProxyTestFactory();
        // TaskCanceledException simulates HttpClient.Timeout firing — the proxy distinguishes
        // this from caller cancellation by checking ctx.RequestAborted.
        factory.Handler.RespondAsync(_ => throw new TaskCanceledException("request timed out"));

        using var client = factory.CreateClient();
        var resp = await client.GetAsync("/api/data/exchanges", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.GatewayTimeout, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("upstream_timeout", doc.GetProperty("code").GetString());
    }

    [Fact]
    public async Task PassthroughGet_5xx_RendersProblemDetails_PreservesUpstreamStatus()
    {
        // Upstream 503 (queue full) on a non-cached endpoint — translated to ProblemDetails
        // with the upstream status preserved (NOT collapsed to 502).
        await using var factory = new DataProxyTestFactory();
        factory.Handler.Respond(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            r.Content = new StringContent("queue full", Encoding.UTF8, "text/plain");
            return r;
        });

        using var client = factory.CreateClient();
        var resp = await client.GetAsync("/api/data/aggregations/j99",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("upstream_error", doc.GetProperty("code").GetString());
    }

    // -------------------------------------------------------------------------
    // T5 — DataProxyCache TTL is ABSOLUTE (not sliding). The 2-second window is hard-coded
    // and must expire on schedule even under continuous read pressure. The frontend's
    // post-completion refetch (use-job-stream.ts setTimeout(2500)) depends on this.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CatalogCache_AbsoluteTtl_ExpiresOnSchedule_NotSliding()
    {
        await using var factory = new DataProxyTestFactory();
        factory.Handler.Respond(_ => JsonResp("""{"exchanges":[]}"""u8.ToArray()));

        using var client = factory.CreateClient();

        // Pre-warm + immediate re-read inside the TTL window — must hit cache (1 upstream call).
        await client.GetByteArrayAsync("/api/data/exchanges", TestContext.Current.CancellationToken);
        await client.GetByteArrayAsync("/api/data/exchanges", TestContext.Current.CancellationToken);
        Assert.Equal(1, ExactPathCallCount(factory, "/api/v1/exchanges"));

        // Wait past the 2-second absolute TTL. If the implementation flipped to sliding,
        // the second read above would have refreshed the entry and this would still hit cache.
        await Task.Delay(TimeSpan.FromMilliseconds(2_300), TestContext.Current.CancellationToken);

        await client.GetByteArrayAsync("/api/data/exchanges", TestContext.Current.CancellationToken);
        Assert.Equal(2, ExactPathCallCount(factory, "/api/v1/exchanges"));
    }

    // -------------------------------------------------------------------------
    // T11 — SSE proxy forwards the caller's Last-Event-ID header to upstream verbatim,
    // so HistoryLoader can replay events past that sequence number for resume (TRD §5.4).
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SseProxy_ForwardsLastEventIdHeader_ToUpstream()
    {
        string? observedLastEventId = null;
        await using var factory = new DataProxyTestFactory();
        factory.Handler.Respond(req =>
        {
            observedLastEventId = req.Headers.TryGetValues("Last-Event-ID", out var values)
                ? string.Join(",", values)
                : null;
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent("event: ping\ndata: {}\n\n"u8.ToArray()),
            };
            resp.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return resp;
        });

        using var client = factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/data/aggregations/j1/progress");
        req.Headers.TryAddWithoutValidation("Last-Event-ID", "42");
        var resp = await client.SendAsync(req, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("42", observedLastEventId);
    }

    // -------------------------------------------------------------------------
    // T12 — POST aggregate that upstream answers with 500 must wrap in ProblemDetails with
    // a stable `code` field. 4xx is forwarded byte-identical (domain payload), but 5xx is
    // server-internal and gets the proxy's stable error envelope.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task PostAggregate_Upstream500_WrapsAsProblemDetails_PreservesStatus()
    {
        await using var factory = new DataProxyTestFactory();
        factory.Handler.Respond(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("internal boom", Encoding.UTF8, "text/plain"),
            };
            return r;
        });

        using var client = factory.CreateClient();
        var resp = await client.PostAsJsonAsync(
            "/api/data/exchanges/binance/assets/BTCUSDT_perp/aggregate",
            new { source_feed_id = "1m", type_code = "EqV" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("upstream_error", doc.GetProperty("code").GetString());
    }

    // -------------------------------------------------------------------------
    // Negative-cache test — confirms aggregation-options is NOT cached
    // -------------------------------------------------------------------------

    [Fact]
    public async Task AggregationOptions_NotCached_HitsUpstreamEachCall()
    {
        await using var factory = new DataProxyTestFactory();
        factory.Handler.Respond(_ => JsonResp("""{"feed_id":"1m","eligible_types":[]}"""u8.ToArray()));

        using var client = factory.CreateClient();
        var path = "/api/data/exchanges/binance/assets/BTCUSDT_perp/feeds/1m/aggregation-options";
        await client.GetByteArrayAsync(path, TestContext.Current.CancellationToken);
        await client.GetByteArrayAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(2, factory.Handler.CallCount("/aggregation-options"));
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Exact-path call counter — distinguishes <c>/api/v1/exchanges</c> from
    /// <c>/api/v1/exchanges/binance/assets/.../aggregate</c> which both contain the substring
    /// <c>/api/v1/exchanges</c>.
    /// </summary>
    private static int ExactPathCallCount(DataProxyTestFactory factory, string exactPath) =>
        factory.Handler.Requests.Count(r => r.RequestUri?.AbsolutePath == exactPath);

    private static HttpResponseMessage JsonResp(
        byte[] body,
        HttpStatusCode status = HttpStatusCode.OK,
        string? locationHeader = null,
        params (string Name, string Value)[] extraHeaders)
    {
        var resp = new HttpResponseMessage(status)
        {
            Content = new ByteArrayContent(body),
        };
        resp.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        if (locationHeader is not null)
            resp.Headers.Location = new Uri(locationHeader, UriKind.Relative);
        foreach (var (n, v) in extraHeaders)
            resp.Headers.TryAddWithoutValidation(n, v);
        return resp;
    }
}
