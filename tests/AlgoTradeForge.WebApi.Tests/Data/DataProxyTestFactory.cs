using System.IO.Pipelines;
using System.Net.Http.Headers;
using AlgoTradeForge.WebApi.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AlgoTradeForge.WebApi.Tests.Data;

/// <summary>
/// Specialized <see cref="WebApplicationFactory{TEntryPoint}"/> for the data-proxy tests.
/// Replaces the typed <see cref="HistoryLoaderClient"/>'s primary HTTP handler with an
/// injected <see cref="DelegatingHandler"/> so each test wires up canned responses instead
/// of spinning a second TestServer for HistoryLoader.
/// </summary>
/// <remarks>
/// <para>
/// Independent of <c>AlgoTradeForgeApiFactory</c> (which is sealed and SQLite/candle-data
/// flavored). Proxy tests don't touch persistence, so the lighter factory keeps startup fast
/// and avoids competing with the canonical Api collection's parallelization-disabled lock.
/// </para>
/// <para>
/// Per-test instance: each test instantiates a fresh factory so the canned-response handler
/// is configured cleanly per scenario.
/// </para>
/// </remarks>
public sealed class DataProxyTestFactory : WebApplicationFactory<Program>
{
    public CapturingHandler Handler { get; } = new();
    public BufferingCapture BufferingCapture { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        // Point CandleStorage at a temp dir so service registrations that depend on it succeed.
        // None of the proxy tests touch this, but several services in Program.cs are eager.
        var tempData = Path.Combine(Path.GetTempPath(), "AlgoTradeForge_DataProxyTests");
        Directory.CreateDirectory(tempData);
        builder.UseSetting("CandleStorage:DataRoot", tempData);
        builder.UseSetting("RunStorage:DatabasePath", Path.Combine(tempData, "ignored.sqlite"));

        builder.ConfigureServices(services =>
        {
            // Replace the primary handler on the typed client. Each test sets Handler.Respond
            // before sending a request.
            services.AddHttpClient<HistoryLoaderClient>()
                    .ConfigurePrimaryHttpMessageHandler(_ => Handler);

            // Wrap the response with a capturing IHttpResponseBodyFeature so SSE tests can
            // assert DisableBuffering() was invoked.
            services.AddSingleton<IStartupFilter>(new BufferingCaptureStartupFilter(BufferingCapture));
        });
    }
}

/// <summary>
/// Records the most recent outbound <see cref="HttpRequestMessage"/> and returns whatever
/// the test wires up via <see cref="Respond"/>. Per-test instance.
/// </summary>
public sealed class CapturingHandler : DelegatingHandler
{
    private readonly List<HttpRequestMessage> _requests = [];
    private Func<HttpRequestMessage, Task<HttpResponseMessage>>? _responder;

    public IReadOnlyList<HttpRequestMessage> Requests => _requests;
    public int CallCount(string pathContains) =>
        _requests.Count(r => r.RequestUri?.AbsolutePath.Contains(pathContains) == true);

    public void Respond(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        _responder = req => Task.FromResult(responder(req));

    public void RespondAsync(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) =>
        _responder = responder;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is not null)
        {
            var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            var copy = new ByteArrayContent(bytes);
            foreach (var h in request.Content.Headers)
                copy.Headers.TryAddWithoutValidation(h.Key, h.Value);
            request.Content = copy;
        }
        _requests.Add(request);
        return _responder is null
            ? new HttpResponseMessage(System.Net.HttpStatusCode.NotImplemented)
            : await _responder(request);
    }
}

/// <summary>Tracks whether <see cref="IHttpResponseBodyFeature.DisableBuffering"/> was invoked.</summary>
public sealed class BufferingCapture
{
    public bool DisableBufferingCalled { get; set; }
}

internal sealed class BufferingCaptureStartupFilter(BufferingCapture capture) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (ctx, nextMw) =>
        {
            var inner = ctx.Features.Get<IHttpResponseBodyFeature>();
            if (inner is not null)
                ctx.Features.Set<IHttpResponseBodyFeature>(new CapturingBodyFeature(inner, capture));
            await nextMw();
        });
        next(app);
    };
}

/// <summary>
/// Forwards every <see cref="IHttpResponseBodyFeature"/> call to the inner feature, recording
/// whether <see cref="DisableBuffering"/> was called. This is the P3-7 assertion target.
/// </summary>
internal sealed class CapturingBodyFeature(IHttpResponseBodyFeature inner, BufferingCapture capture)
    : IHttpResponseBodyFeature
{
    public Stream Stream => inner.Stream;
    public PipeWriter Writer => inner.Writer;

    public void DisableBuffering()
    {
        capture.DisableBufferingCalled = true;
        inner.DisableBuffering();
    }

    public Task StartAsync(CancellationToken cancellationToken = default) =>
        inner.StartAsync(cancellationToken);
    public Task CompleteAsync() => inner.CompleteAsync();
    public Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default) =>
        inner.SendFileAsync(path, offset, count, cancellationToken);
}
