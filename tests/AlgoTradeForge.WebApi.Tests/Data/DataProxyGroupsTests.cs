using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Xunit;

namespace AlgoTradeForge.WebApi.Tests.Data;

/// <summary>
/// Proxy tests for the groups + desired-state pass-through routes added to the main WebApi.
/// Verifies: ETag round-trip on GET/PUT; 409/422 pass-through; validate POST body forwarded
/// verbatim; desired-state query string forwarded.
/// </summary>
public sealed class DataProxyGroupsTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static HttpResponseMessage JsonResp(
        byte[] body,
        HttpStatusCode code = HttpStatusCode.OK,
        string? etag = null)
    {
        var resp = new HttpResponseMessage(code) { Content = new ByteArrayContent(body) };
        resp.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        if (etag is not null)
            resp.Headers.ETag = new EntityTagHeaderValue($"\"{etag}\"");
        return resp;
    }

    // -------------------------------------------------------------------------
    // GET /api/data/groups — list
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetGroups_RoundTripsBytes_HitsUpstreamListPath()
    {
        var canonical = """{"groups":[{"name":"g1","enabled":true,"exchanges":["binance"],"symbol_count":1,"feed_count":2,"etag":"v1"}]}"""u8.ToArray();
        await using var factory = new DataProxyTestFactory();
        factory.Handler.Respond(_ => JsonResp(canonical));

        using var client = factory.CreateClient();
        var bytes = await client.GetByteArrayAsync("/api/data/groups", TestContext.Current.CancellationToken);

        Assert.Equal(canonical, bytes);
        var upstream = Assert.Single(factory.Handler.Requests);
        Assert.Equal("/api/v1/groups", upstream.RequestUri!.AbsolutePath);
    }

    // -------------------------------------------------------------------------
    // GET /api/data/groups/{name} — ETag round-trip (upstream → client)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetGroup_ForwardsETagResponseHeader()
    {
        var body = """{"name":"g1","enabled":true}"""u8.ToArray();
        await using var factory = new DataProxyTestFactory();
        factory.Handler.Respond(_ => JsonResp(body, etag: "v1abc"));

        using var client = factory.CreateClient();
        var resp = await client.GetAsync("/api/data/groups/g1", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("\"v1abc\"", resp.Headers.ETag?.Tag);
    }

    [Fact]
    public async Task GetGroup_404_ForwardedVerbatim()
    {
        var body404 = """{"error":"group_not_found","name":"missing"}"""u8.ToArray();
        await using var factory = new DataProxyTestFactory();
        factory.Handler.Respond(_ => JsonResp(body404, HttpStatusCode.NotFound));

        using var client = factory.CreateClient();
        var resp = await client.GetAsync("/api/data/groups/missing", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var bytes = await resp.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(body404, bytes);
    }

    // -------------------------------------------------------------------------
    // PUT /api/data/groups/{name} — If-Match forwarded upstream, 409/422 pass-through
    // -------------------------------------------------------------------------

    [Fact]
    public async Task PutGroup_ForwardsIfMatchHeader_ToUpstream()
    {
        string? observedIfMatch = null;
        await using var factory = new DataProxyTestFactory();
        factory.Handler.RespondAsync(req =>
        {
            observedIfMatch = req.Headers.TryGetValues("If-Match", out var vals) ? string.Join(",", vals) : null;
            return Task.FromResult(JsonResp("""{"etag":"v2"}"""u8.ToArray()));
        });

        using var client = factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Put, "/api/data/groups/g1")
        {
            Content = new StringContent(
                """{"name":"g1","enabled":true,"exchanges":[],"assets":{"symbols":[]},"feeds":[]}""",
                Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("If-Match", "\"v1abc\"");
        var resp = await client.SendAsync(req, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("\"v1abc\"", observedIfMatch);
    }

    [Fact]
    public async Task PutGroup_409_ConcurrencyConflict_ForwardedVerbatim()
    {
        var body409 = """{"error":"concurrency_conflict"}"""u8.ToArray();
        await using var factory = new DataProxyTestFactory();
        factory.Handler.Respond(_ => JsonResp(body409, HttpStatusCode.Conflict));

        using var client = factory.CreateClient();
        var resp = await client.PutAsJsonAsync(
            "/api/data/groups/g1",
            new { name = "g1", enabled = true, exchanges = Array.Empty<string>() },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var bytes = await resp.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(body409, bytes);
    }

    [Fact]
    public async Task PutGroup_422_ValidationError_ForwardedVerbatim()
    {
        var body422 = """{"error":"validation_failed","errors":["name is invalid"]}"""u8.ToArray();
        await using var factory = new DataProxyTestFactory();
        factory.Handler.Respond(_ => JsonResp(body422, HttpStatusCode.UnprocessableEntity));

        using var client = factory.CreateClient();
        var resp = await client.PutAsJsonAsync(
            "/api/data/groups/g1",
            new { name = "g1" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        var bytes = await resp.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(body422, bytes);
    }

    // -------------------------------------------------------------------------
    // DELETE /api/data/groups/{name} — 204 + 404 pass-through
    // -------------------------------------------------------------------------

    [Fact]
    public async Task DeleteGroup_Returns204_ForwardsToUpstreamPath()
    {
        string? observedPath = null;
        await using var factory = new DataProxyTestFactory();
        factory.Handler.RespondAsync(req =>
        {
            observedPath = req.RequestUri!.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        });

        using var client = factory.CreateClient();
        var resp = await client.DeleteAsync("/api/data/groups/g1", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        Assert.Equal("/api/v1/groups/g1", observedPath);
    }

    [Fact]
    public async Task DeleteGroup_404_ForwardedVerbatim()
    {
        var body404 = """{"error":"group_not_found","name":"missing"}"""u8.ToArray();
        await using var factory = new DataProxyTestFactory();
        factory.Handler.Respond(_ => JsonResp(body404, HttpStatusCode.NotFound));

        using var client = factory.CreateClient();
        var resp = await client.DeleteAsync("/api/data/groups/missing", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var bytes = await resp.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(body404, bytes);
    }

    // -------------------------------------------------------------------------
    // POST /api/data/groups/validate — body forwarded verbatim; 422 pass-through
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ValidateGroup_PostBodyForwardedVerbatim()
    {
        const string requestBody =
            """{"name":"g1","enabled":true,"exchanges":["binance"],"assets":{"symbols":["BTCUSDT"]},"feeds":[{"feedName":"candles","interval":"1h"}]}""";
        var responseBody =
            """{"errors":[],"expansion":{"tuple_count":1,"unsupported":[],"conflicts":[],"per_exchange":[],"already_materialized":0}}"""u8.ToArray();
        await using var factory = new DataProxyTestFactory();
        factory.Handler.Respond(_ => JsonResp(responseBody));

        using var client = factory.CreateClient();
        var resp = await client.PostAsync(
            "/api/data/groups/validate",
            new StringContent(requestBody, Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var upstream = Assert.Single(factory.Handler.Requests);
        Assert.Equal("/api/v1/groups/validate", upstream.RequestUri!.AbsolutePath);
        var upstreamBody = await upstream.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("\"BTCUSDT\"", upstreamBody);
    }

    [Fact]
    public async Task ValidateGroup_422_ForwardedVerbatim()
    {
        var body422 = """{"errors":["name is required"],"expansion":null}"""u8.ToArray();
        await using var factory = new DataProxyTestFactory();
        factory.Handler.Respond(_ => JsonResp(body422, HttpStatusCode.UnprocessableEntity));

        using var client = factory.CreateClient();
        var resp = await client.PostAsJsonAsync(
            "/api/data/groups/validate",
            new { enabled = true },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        var bytes = await resp.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(body422, bytes);
    }

    // -------------------------------------------------------------------------
    // GET /api/data/desired-state — query string forwarded
    // -------------------------------------------------------------------------

    [Fact]
    public async Task DesiredState_ForwardsQueryString_ToUpstream()
    {
        var responseBody =
            """{"computed_at":"2024-01-01T00:00:00Z","tuples":[],"orphaned":[],"orphaned_total":0,"conflicts":[]}"""u8.ToArray();
        await using var factory = new DataProxyTestFactory();
        factory.Handler.Respond(_ => JsonResp(responseBody));

        using var client = factory.CreateClient();
        var bytes = await client.GetByteArrayAsync(
            "/api/data/desired-state?exchange=binance",
            TestContext.Current.CancellationToken);

        Assert.Equal(responseBody, bytes);
        var upstream = Assert.Single(factory.Handler.Requests);
        Assert.Equal("/api/v1/desired-state", upstream.RequestUri!.AbsolutePath);
        Assert.Contains("exchange=binance", upstream.RequestUri.Query);
    }

    [Fact]
    public async Task DesiredState_NoQueryString_ForwardsCleanPath()
    {
        var responseBody = """{"computed_at":"2024-01-01T00:00:00Z","tuples":[],"orphaned":[],"orphaned_total":0,"conflicts":[]}"""u8.ToArray();
        await using var factory = new DataProxyTestFactory();
        factory.Handler.Respond(_ => JsonResp(responseBody));

        using var client = factory.CreateClient();
        await client.GetByteArrayAsync("/api/data/desired-state", TestContext.Current.CancellationToken);

        var upstream = Assert.Single(factory.Handler.Requests);
        Assert.Equal("/api/v1/desired-state", upstream.RequestUri!.AbsolutePath);
        Assert.True(string.IsNullOrEmpty(upstream.RequestUri.Query));
    }
}
