using Microsoft.Playwright;

namespace AlgoTradeForge.WebApi.PlaywrightTests.Infrastructure;

[CollectionDefinition("E2E")]
public sealed class PlaywrightTestCollection : ICollectionFixture<PlaywrightFixture>;

public abstract class PlaywrightTestBase : IAsyncLifetime
{
    private readonly PlaywrightFixture _fixture;
    private IBrowserContext? _context;
    private readonly List<string> _consoleErrors = [];
    private readonly List<string> _networkFailures = [];

    // Patterns to ignore in console/network error monitoring
    private static readonly string[] ConsoleErrorAllowlist =
    [
        "favicon.ico",
        "HMR",
        "hot-update",
        "webpack",
        "Fast Refresh",
        "__nextjs",
        "next-router",
        "hydration",
        "Failed to load resource",   // transient 404s during navigation (e.g. events endpoint)
        "React DevTools",
        "Download the React DevTools",
    ];

    private static readonly string[] NetworkFailureAllowlist =
    [
        "favicon.ico",
        "hot-update",
        "__webpack_hmr",
        "_next/webpack",
        "on-demand-entries",
        "_rsc=",                    // React Server Component requests aborted during navigation
    ];

    // Network failure reasons to ignore (checked against request.Failure)
    private static readonly string[] NetworkFailureReasonAllowlist =
    [
        "ERR_ABORTED",             // Requests aborted by navigation or component unmount
    ];

    protected IPage Page { get; private set; } = null!;
    protected string BaseUrl => _fixture.FrontendBaseUrl;

    protected PlaywrightTestBase(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
        });

        Page = await _context.NewPageAsync();

        // Monitor console errors
        Page.Console += (_, msg) =>
        {
            if (msg.Type == "error" &&
                !ConsoleErrorAllowlist.Any(a =>
                    msg.Text.Contains(a, StringComparison.OrdinalIgnoreCase)))
            {
                _consoleErrors.Add(msg.Text);
            }
        };

        // Monitor network failures
        Page.RequestFailed += (_, request) =>
        {
            var url = request.Url;
            var failure = request.Failure ?? "";
            if (!NetworkFailureAllowlist.Any(a =>
                    url.Contains(a, StringComparison.OrdinalIgnoreCase)) &&
                !NetworkFailureReasonAllowlist.Any(a =>
                    failure.Contains(a, StringComparison.OrdinalIgnoreCase)))
            {
                _networkFailures.Add($"{request.Method} {url}: {failure}");
            }
        };
    }

    public async Task DisposeAsync()
    {
        if (_context is not null)
            await _context.CloseAsync();
    }

    /// <summary>
    /// Assert that no unexpected console errors occurred during the test.
    /// </summary>
    protected void AssertNoConsoleErrors()
    {
        if (_consoleErrors.Count > 0)
        {
            Assert.Fail($"Unexpected console errors ({_consoleErrors.Count}):\n" +
                         string.Join("\n", _consoleErrors));
        }
    }

    /// <summary>
    /// Assert that no unexpected network request failures occurred during the test.
    /// </summary>
    protected void AssertNoNetworkFailures()
    {
        if (_networkFailures.Count > 0)
        {
            Assert.Fail($"Unexpected network failures ({_networkFailures.Count}):\n" +
                         string.Join("\n", _networkFailures));
        }
    }
}
