using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace AlgoTradeForge.WebApi.Tests.Data;

public sealed class DataProxyLoadsTests
{
    private static HttpResponseMessage JsonResp(byte[] body, HttpStatusCode code = HttpStatusCode.OK)
    {
        var resp = new HttpResponseMessage(code) { Content = new ByteArrayContent(body) };
        resp.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
        return resp;
    }

    [Fact]
    public async Task Coverage_ForwardsQueryString_AndRoundTripsBytes()
    {
        var canonical = """{"asset_dir":"x","feeds":[{"feed_name":"candles","interval":"1h","covered_months":["2024-01"],"first_timestamp":null,"last_timestamp":1706745600000}]}"""u8.ToArray();
        await using var factory = new DataProxyTestFactory();
        factory.Handler.Respond(_ => JsonResp(canonical));

        using var client = factory.CreateClient();
        var bytes = await client.GetByteArrayAsync(
            "/api/data/coverage?exchange=binance&symbol=BTCUSDT&asset_type=perpetual",
            TestContext.Current.CancellationToken);

        Assert.Equal(canonical, bytes);
        var upstream = Assert.Single(factory.Handler.Requests);
        Assert.Equal("/api/v1/coverage", upstream.RequestUri!.AbsolutePath);
        Assert.Contains("asset_type=perpetual", upstream.RequestUri.Query);
    }

    [Fact]
    public async Task PostLoads_Forwards202AndBody()
    {
        await using var factory = new DataProxyTestFactory();
        factory.Handler.Respond(req =>
            JsonResp("""{"job_id":"abc123"}"""u8.ToArray(), HttpStatusCode.Accepted));

        using var client = factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/data/loads", new
        {
            exchange = "binance", symbol = "BTCUSDT", asset_type = "perpetual",
            feed_name = "candles", interval = "1h", from = "2024-01-01", to = "2024-03-31",
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        Assert.Equal("""{"job_id":"abc123"}""", await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var upstream = Assert.Single(factory.Handler.Requests);
        Assert.Equal("/api/v1/loads", upstream.RequestUri!.AbsolutePath);
        // Body forwarded byte-identical (snake_case preserved).
        Assert.Contains("\"asset_type\":\"perpetual\"", await upstream.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PostLoads_Forwards409Verbatim()
    {
        await using var factory = new DataProxyTestFactory();
        factory.Handler.Respond(_ =>
            JsonResp("""{"error":"symbol_busy","active_job_id":"j9"}"""u8.ToArray(), HttpStatusCode.Conflict));
        using var client = factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/data/loads", new { }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Contains("active_job_id", await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PostLoads_MalformedJson_Returns400NotThrough()
    {
        await using var factory = new DataProxyTestFactory();
        using var client = factory.CreateClient();
        var resp = await client.PostAsync("/api/data/loads",
            new StringContent("{not json", System.Text.Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Empty(factory.Handler.Requests); // never reached upstream
    }

    [Fact]
    public async Task GetLoad_PassesThrough_And5xxTranslates()
    {
        await using var factory = new DataProxyTestFactory();
        factory.Handler.Respond(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
            { Content = new StringContent("boom") });
        using var client = factory.CreateClient();
        var resp = await client.GetAsync("/api/data/loads/abc123", TestContext.Current.CancellationToken);
        // DataProxyProblem.UpstreamError preserves upstream status (not collapsed to 502).
        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
    }
}
