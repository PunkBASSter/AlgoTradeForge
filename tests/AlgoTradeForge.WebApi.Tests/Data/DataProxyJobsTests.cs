using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AlgoTradeForge.WebApi.Tests.Data;

/// <summary>
/// Proxy-level tests for the unified jobs + materialize routes added in M6.1.
/// Mirrors the shape of <see cref="DataProxyLoadsTests"/> and the SSE section of
/// <see cref="DataProxyTests"/>.
/// </summary>
public sealed class DataProxyJobsTests
{
    private static HttpResponseMessage JsonResp(byte[] body, HttpStatusCode code = HttpStatusCode.OK)
    {
        var resp = new HttpResponseMessage(code) { Content = new ByteArrayContent(body) };
        resp.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return resp;
    }

    // -------------------------------------------------------------------------
    // GET /jobs — query-string forwarding + byte round-trip
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetJobs_ForwardsQueryString_AndRoundTripsBytes()
    {
        var canonical = """[{"job_id":"j1","kind":"materialize","state":"running"}]"""u8.ToArray();
        await using var factory = new DataProxyTestFactory();
        factory.Handler.Respond(_ => JsonResp(canonical));

        using var client = factory.CreateClient();
        var bytes = await client.GetByteArrayAsync(
            "/api/data/jobs?kind=materialize&state=running",
            TestContext.Current.CancellationToken);

        Assert.Equal(canonical, bytes);
        var upstream = Assert.Single(factory.Handler.Requests);
        Assert.Equal("/api/v1/jobs", upstream.RequestUri!.AbsolutePath);
        Assert.Contains("kind=materialize", upstream.RequestUri.Query);
        Assert.Contains("state=running", upstream.RequestUri.Query);
    }

    [Fact]
    public async Task GetJobs_NoQueryString_ForwardsToBasePath()
    {
        await using var factory = new DataProxyTestFactory();
        factory.Handler.Respond(_ => JsonResp("[]"u8.ToArray()));

        using var client = factory.CreateClient();
        await client.GetByteArrayAsync("/api/data/jobs", TestContext.Current.CancellationToken);

        var upstream = Assert.Single(factory.Handler.Requests);
        Assert.Equal("/api/v1/jobs", upstream.RequestUri!.AbsolutePath);
    }

    // -------------------------------------------------------------------------
    // GET /jobs/{id}
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetJob_ForwardsToUpstreamJobPath()
    {
        var canonical = """{"job_id":"mat-1","kind":"materialize","state":"complete"}"""u8.ToArray();
        await using var factory = new DataProxyTestFactory();
        factory.Handler.Respond(_ => JsonResp(canonical));

        using var client = factory.CreateClient();
        var bytes = await client.GetByteArrayAsync(
            "/api/data/jobs/mat-1",
            TestContext.Current.CancellationToken);

        Assert.Equal(canonical, bytes);
        var upstream = Assert.Single(factory.Handler.Requests);
        Assert.Equal("/api/v1/jobs/mat-1", upstream.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task GetJob_5xx_TranslatesToProblemDetails()
    {
        await using var factory = new DataProxyTestFactory();
        factory.Handler.Respond(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
            { Content = new StringContent("boom") });

        using var client = factory.CreateClient();
        var resp = await client.GetAsync("/api/data/jobs/x", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("upstream_error", doc.GetProperty("code").GetString());
    }

    // -------------------------------------------------------------------------
    // POST /materialize
    // -------------------------------------------------------------------------

    [Fact]
    public async Task PostMaterialize_Forwards202AndBody()
    {
        await using var factory = new DataProxyTestFactory();
        factory.Handler.Respond(_ => JsonResp(
            """{"job_id":"mat-99","state":"queued"}"""u8.ToArray(), HttpStatusCode.Accepted));

        using var client = factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/data/materialize", new
        {
            group_name = "g1",
            feeds = new[] { "candles", "funding_rate" },
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        Assert.Equal(
            """{"job_id":"mat-99","state":"queued"}""",
            await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var upstream = Assert.Single(factory.Handler.Requests);
        Assert.Equal("/api/v1/materialize", upstream.RequestUri!.AbsolutePath);
        Assert.Contains("\"group_name\"", await upstream.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PostMaterialize_Forwards409Verbatim()
    {
        await using var factory = new DataProxyTestFactory();
        factory.Handler.Respond(_ => JsonResp(
            """{"error":"group_busy","group_name":"g1"}"""u8.ToArray(), HttpStatusCode.Conflict));

        using var client = factory.CreateClient();
        var resp = await client.PostAsJsonAsync(
            "/api/data/materialize", new { group_name = "g1" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Contains("group_busy", await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PostMaterialize_MalformedJson_Returns400NotThrough()
    {
        await using var factory = new DataProxyTestFactory();
        using var client = factory.CreateClient();
        var resp = await client.PostAsync(
            "/api/data/materialize",
            new StringContent("{not json", Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Empty(factory.Handler.Requests);
    }

    // -------------------------------------------------------------------------
    // DELETE /jobs/{id}
    // -------------------------------------------------------------------------

    [Fact]
    public async Task DeleteJob_ForwardsDelete_AndReturns204_OnSuccess()
    {
        var observedPath = "";
        await using var factory = new DataProxyTestFactory();
        factory.Handler.RespondAsync(req =>
        {
            observedPath = req.RequestUri!.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        });

        using var client = factory.CreateClient();
        var resp = await client.DeleteAsync(
            "/api/data/jobs/mat-42",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        Assert.Equal("/api/v1/jobs/mat-42", observedPath);
    }

    [Fact]
    public async Task DeleteJob_Forwards404_WhenJobUnknown()
    {
        await using var factory = new DataProxyTestFactory();
        factory.Handler.RespondAsync(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent(
                """{"error":"job_not_found_or_expired","job_id":"missing"}""",
                Encoding.UTF8, "application/json"),
        }));

        using var client = factory.CreateClient();
        var resp = await client.DeleteAsync(
            "/api/data/jobs/missing", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Contains("job_not_found_or_expired",
            await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteJob_Forwards409_WhenJobAlreadyTerminal()
    {
        await using var factory = new DataProxyTestFactory();
        factory.Handler.RespondAsync(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent(
                """{"code":"job_already_terminal","job_id":"j1","state":"complete"}""",
                Encoding.UTF8, "application/json"),
        }));

        using var client = factory.CreateClient();
        var resp = await client.DeleteAsync("/api/data/jobs/j1", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Contains("job_already_terminal",
            await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    // -------------------------------------------------------------------------
    // GET /jobs/{id}/progress — SSE pass-through (mirrors DataProxyTests P3-7)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task JobSsePassThrough_PreservesContentType_AndDisablesBuffering()
    {
        var sseBody = """
id: 1
event: started
data: {"job_id":"mat-1","group_name":"g1"}

id: 2
event: progress
data: {"feeds_done":1,"feeds_total":3}


""".ReplaceLineEndings("\n");
        var sseBytes = Encoding.UTF8.GetBytes(sseBody);

        await using var factory = new DataProxyTestFactory();
        factory.Handler.Respond(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new ByteArrayContent(sseBytes) };
            resp.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return resp;
        });

        using var client = factory.CreateClient();
        var resp = await client.GetAsync(
            "/api/data/jobs/mat-1/progress",
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
    public async Task JobSsePassThrough_410Gone_ForwardsStatusAndBody()
    {
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
            "/api/data/jobs/old/progress", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Gone, resp.StatusCode);
        Assert.Equal(body, await resp.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task JobSseProxy_ForwardsLastEventIdHeader_ToUpstream()
    {
        string? observedLastEventId = null;
        await using var factory = new DataProxyTestFactory();
        factory.Handler.Respond(req =>
        {
            observedLastEventId = req.Headers.TryGetValues("Last-Event-ID", out var values)
                ? string.Join(",", values)
                : null;
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new ByteArrayContent("event: ping\ndata: {}\n\n"u8.ToArray()) };
            resp.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return resp;
        });

        using var client = factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/data/jobs/mat-5/progress");
        req.Headers.TryAddWithoutValidation("Last-Event-ID", "99");
        var resp = await client.SendAsync(req, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("99", observedLastEventId);
    }
}
