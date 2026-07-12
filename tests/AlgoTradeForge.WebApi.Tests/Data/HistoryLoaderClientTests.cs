using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AlgoTradeForge.WebApi.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.WebApi.Tests.Data;

/// <summary>
/// Pure unit tests for the typed <see cref="HistoryLoaderClient"/> (Phase 3 / P3-1). Uses a
/// captured-request <see cref="DelegatingHandler"/> instead of <c>WebApplicationFactory</c>
/// to keep the test surface narrow: this verifies the client's outbound shape, not the
/// proxy's response handling (covered in S5).
/// </summary>
public sealed class HistoryLoaderClientTests
{
    private static (HistoryLoaderClient client, CapturingHandler handler) BuildClient(
        string? baseUrlOverride = null, TimeSpan? timeoutOverride = null)
    {
        var inMem = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HistoryLoader:BaseUrl"] = baseUrlOverride ?? "http://history.test",
                ["HistoryLoader:RequestTimeout"] =
                    (timeoutOverride ?? TimeSpan.FromSeconds(30)).ToString(),
            })
            .Build();

        var services = new ServiceCollection();
        services.AddHistoryLoaderClient(inMem);

        // Replace the named-client's primary handler with our capturing one. The named-client
        // factory injects this into the typed client's HttpClient.
        var handler = new CapturingHandler();
        services.AddHttpClient<HistoryLoaderClient>().ConfigurePrimaryHttpMessageHandler(() => handler);

        var sp = services.BuildServiceProvider();
        return (sp.GetRequiredService<HistoryLoaderClient>(), handler);
    }

    [Fact]
    public async Task GetAsync_AppendsRelativePathToBaseUrl()
    {
        var (client, handler) = BuildClient();
        handler.Respond(req => Json("{\"ok\":true}"));

        await client.GetAsync("/api/v1/exchanges", TestContext.Current.CancellationToken);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal("http://history.test/api/v1/exchanges", handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task GetAsync_TrailingSlashOnBaseUrl_NormalizedAtBindTime()
    {
        // User pasted a trailing slash; the registration normalizes by trimming + re-appending one.
        // Result: same final URL as without the trailing slash.
        var (client, handler) = BuildClient(baseUrlOverride: "http://history.test/");
        handler.Respond(req => Json("{}"));

        await client.GetAsync("/api/v1/assets", TestContext.Current.CancellationToken);

        Assert.Equal("http://history.test/api/v1/assets", handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task PostJsonAsync_SerializesBodyAsJson_ContentTypeApplicationJson()
    {
        var (client, handler) = BuildClient();
        handler.Respond(req => new HttpResponseMessage(HttpStatusCode.Accepted));

        var body = JsonSerializer.SerializeToElement(new { source_feed_id = "1m", type_code = "EqV" });
        await client.PostJsonAsync("/api/v1/exchanges/Binance/assets/BTCUSDT/aggregate", body,
            TestContext.Current.CancellationToken);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("application/json", handler.LastRequest.Content!.Headers.ContentType!.MediaType);

        var bodyText = await handler.LastRequest.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("source_feed_id", bodyText);
        Assert.Contains("EqV", bodyText);
    }

    [Fact]
    public async Task DeleteAsync_SendsDelete()
    {
        var (client, handler) = BuildClient();
        handler.Respond(req => new HttpResponseMessage(HttpStatusCode.NoContent));

        await client.DeleteAsync("/api/v1/exchanges/Binance/assets/BTCUSDT/feeds/EqV_1m_1000",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Delete, handler.LastRequest!.Method);
    }

    [Fact]
    public async Task OpenProgressStreamAsync_ForwardsLastEventIdHeader()
    {
        var (client, handler) = BuildClient();
        handler.Respond(req =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK);
            resp.Content = new StringContent("", Encoding.UTF8, "text/event-stream");
            return resp;
        });

        await client.OpenProgressStreamAsync(jobId: "abc123", lastEventId: "42",
            TestContext.Current.CancellationToken);

        Assert.True(handler.LastRequest!.Headers.TryGetValues("Last-Event-ID", out var values));
        Assert.Equal("42", values!.Single());
    }

    [Fact]
    public async Task OpenProgressStreamAsync_NullLastEventId_OmitsHeader()
    {
        var (client, handler) = BuildClient();
        handler.Respond(req => new HttpResponseMessage(HttpStatusCode.OK));

        await client.OpenProgressStreamAsync(jobId: "abc123", lastEventId: null,
            TestContext.Current.CancellationToken);

        Assert.False(handler.LastRequest!.Headers.TryGetValues("Last-Event-ID", out _));
    }

    [Fact]
    public async Task OpenProgressStreamAsync_AcceptsTextEventStream()
    {
        var (client, handler) = BuildClient();
        handler.Respond(req => new HttpResponseMessage(HttpStatusCode.OK));

        await client.OpenProgressStreamAsync("abc", null, TestContext.Current.CancellationToken);

        Assert.Contains(handler.LastRequest!.Headers.Accept,
            h => h.MediaType == "text/event-stream");
    }

    [Fact]
    public async Task OpenProgressStreamAsync_PathEncodesJobId()
    {
        var (client, handler) = BuildClient();
        handler.Respond(req => new HttpResponseMessage(HttpStatusCode.OK));

        await client.OpenProgressStreamAsync("job-12-abc", null, TestContext.Current.CancellationToken);

        Assert.EndsWith("/api/v1/aggregations/job-12-abc/progress",
            handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task OpenJobProgressStreamAsync_ForwardsLastEventIdHeader()
    {
        var (client, handler) = BuildClient();
        handler.Respond(req =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK);
            resp.Content = new StringContent("", Encoding.UTF8, "text/event-stream");
            return resp;
        });

        await client.OpenJobProgressStreamAsync(jobId: "j99", lastEventId: "7",
            TestContext.Current.CancellationToken);

        Assert.True(handler.LastRequest!.Headers.TryGetValues("Last-Event-ID", out var values));
        Assert.Equal("7", values!.Single());
    }

    [Fact]
    public async Task OpenJobProgressStreamAsync_NullLastEventId_OmitsHeader()
    {
        var (client, handler) = BuildClient();
        handler.Respond(req => new HttpResponseMessage(HttpStatusCode.OK));

        await client.OpenJobProgressStreamAsync(jobId: "j99", lastEventId: null,
            TestContext.Current.CancellationToken);

        Assert.False(handler.LastRequest!.Headers.TryGetValues("Last-Event-ID", out _));
    }

    [Fact]
    public async Task OpenJobProgressStreamAsync_AcceptsTextEventStream()
    {
        var (client, handler) = BuildClient();
        handler.Respond(req => new HttpResponseMessage(HttpStatusCode.OK));

        await client.OpenJobProgressStreamAsync("j99", null, TestContext.Current.CancellationToken);

        Assert.Contains(handler.LastRequest!.Headers.Accept,
            h => h.MediaType == "text/event-stream");
    }

    [Fact]
    public async Task OpenJobProgressStreamAsync_PathEncodesJobId()
    {
        var (client, handler) = BuildClient();
        handler.Respond(req => new HttpResponseMessage(HttpStatusCode.OK));

        await client.OpenJobProgressStreamAsync("job-mat-123", null, TestContext.Current.CancellationToken);

        Assert.EndsWith("/api/v1/jobs/job-mat-123/progress",
            handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task GetJobs_WithQueryString_AppendsToPath()
    {
        var (client, handler) = BuildClient();
        handler.Respond(req => Json("[]"));

        await client.GetJobs("?kind=materialize&state=running", TestContext.Current.CancellationToken);

        Assert.Contains("/api/v1/jobs", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Contains("kind=materialize", handler.LastRequest.RequestUri!.Query);
    }

    [Fact]
    public async Task GetJobs_NullQueryString_UsesBasePath()
    {
        var (client, handler) = BuildClient();
        handler.Respond(req => Json("[]"));

        await client.GetJobs(null, TestContext.Current.CancellationToken);

        Assert.Equal("/api/v1/jobs", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal("", handler.LastRequest.RequestUri!.Query);
    }

    [Fact]
    public async Task GetJob_PathEncodesJobId()
    {
        var (client, handler) = BuildClient();
        handler.Respond(req => Json("{}"));

        await client.GetJob("mat-job-456", TestContext.Current.CancellationToken);

        Assert.EndsWith("/api/v1/jobs/mat-job-456", handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task DeleteJob_SendsDelete_ToJobsPath()
    {
        var (client, handler) = BuildClient();
        handler.Respond(req => new HttpResponseMessage(HttpStatusCode.NoContent));

        await client.DeleteJob("mat-job-789", TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Delete, handler.LastRequest!.Method);
        Assert.EndsWith("/api/v1/jobs/mat-job-789", handler.LastRequest.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task PostMaterialize_SerializesBodyAsJson_ToMaterializePath()
    {
        var (client, handler) = BuildClient();
        handler.Respond(req => new HttpResponseMessage(HttpStatusCode.Accepted));

        var body = JsonSerializer.SerializeToElement(new { group_name = "my-group", feeds = new[] { "candles" } });
        await client.PostMaterialize(body, TestContext.Current.CancellationToken);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.EndsWith("/api/v1/materialize", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Equal("application/json", handler.LastRequest.Content!.Headers.ContentType!.MediaType);
        var bodyText = await handler.LastRequest.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("group_name", bodyText);
    }

    private static HttpResponseMessage Json(string payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(payload, Encoding.UTF8, "application/json"),
    };

    /// <summary>
    /// Records the most recent outbound <see cref="HttpRequestMessage"/> and returns whatever
    /// the test wires up via <see cref="Respond"/>. Single-shot per test invocation —
    /// concurrent calls from one test would race.
    /// </summary>
    private sealed class CapturingHandler : DelegatingHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        private Func<HttpRequestMessage, HttpResponseMessage>? _responder;

        public void Respond(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
            _responder = responder;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Materialize content NOW so the test can read it after the test method returns
            // (DelegatingHandler disposes the request when SendAsync completes).
            if (request.Content is not null)
            {
                var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
                var copy = new ByteArrayContent(bytes);
                foreach (var h in request.Content.Headers)
                    copy.Headers.TryAddWithoutValidation(h.Key, h.Value);
                request.Content = copy;
            }
            LastRequest = request;
            return _responder?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
