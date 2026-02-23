using AlgoTradeForge.WebApi.PlaywrightTests.Components;
using Microsoft.Playwright;

namespace AlgoTradeForge.WebApi.PlaywrightTests.Pages;

/// <summary>
/// Encapsulates all debug session interactions.
/// </summary>
public sealed class DebugPage(IPage page, string baseUrl)
{
    public CodeMirrorEditor Editor { get; } = new(page);
    public DebugToolbar Toolbar { get; } = new(page);

    public async Task NavigateAsync()
    {
        await page.GotoAsync(baseUrl + "/debug", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle,
            Timeout = 30_000,
        });
    }

    public async Task StartSessionAsync()
    {
        await page.GetByRole(AriaRole.Button, new() { Name = "Start Debug Session" }).ClickAsync();
    }

    public async Task WaitForToolbarAsync(int timeout = 30_000)
    {
        await page.GetByTestId("debug-toolbar").WaitForAsync(new() { Timeout = timeout });
    }

    public async Task WaitForMetricsVisibleAsync(int timeout = 5_000)
    {
        await page.GetByText("Session Metrics").WaitForAsync(new() { Timeout = timeout });
    }

    public async Task AssertHasMetricText(string text)
    {
        var bodyText = await page.TextContentAsync("body");
        Assert.Contains(text, bodyText, StringComparison.OrdinalIgnoreCase);
    }

    public async Task WaitForIdleAsync(int timeout = 10_000)
    {
        await page.GetByRole(AriaRole.Button, new() { Name = "Start Debug Session" })
            .WaitForAsync(new() { Timeout = timeout });
    }
}
