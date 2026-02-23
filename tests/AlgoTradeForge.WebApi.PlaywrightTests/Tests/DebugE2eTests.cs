using AlgoTradeForge.WebApi.PlaywrightTests.Infrastructure;
using Microsoft.Playwright;

namespace AlgoTradeForge.WebApi.PlaywrightTests.Tests;

[Collection("E2E")]
public sealed class DebugE2eTests(PlaywrightFixture fixture) : PlaywrightTestBase(fixture)
{
    private const string DebugConfig = """
        {
          "assetName": "BTCUSDT",
          "exchange": "Binance",
          "strategyName": "ZigZagBreakout",
          "initialCash": 10000,
          "startTime": "2025-01-01T00:00:00Z",
          "endTime": "2025-01-05T00:00:00Z",
          "timeFrame": "01:00:00"
        }
        """;

    [Fact]
    public async Task DebugSessionLifecycle_StartStepPlayPauseStop()
    {
        // Navigate to the debug page
        await NavigateAndWaitAsync("/debug");

        // Wait for the CodeMirror editor to appear
        await Page.Locator(".cm-editor").WaitForAsync(new() { Timeout = 10_000 });

        // Set the debug session config JSON
        await SetCodeMirrorContentAsync(DebugConfig);

        // Click "Start Debug Session" to begin
        var startButton = Page.GetByRole(AriaRole.Button,
            new() { Name = "Start Debug Session" });
        await startButton.ClickAsync();

        // Wait for the debug toolbar to appear (session becomes "active")
        // Use "To Next Bar" as the sentinel — it's unique and only visible when toolbar is active
        var toNextBarButton = Page.GetByRole(AriaRole.Button,
            new() { Name = "To Next Bar", Exact = true });
        await toNextBarButton.WaitForAsync(new() { Timeout = 30_000 });

        // Execute "Next" — step one event (use Exact to avoid matching "To Next ...")
        var nextButton = Page.GetByRole(AriaRole.Button,
            new() { Name = "Next", Exact = true });
        await nextButton.ClickAsync();
        await Task.Delay(500);

        // Execute "To Next Bar"
        await toNextBarButton.ClickAsync();
        await Task.Delay(500);

        // Click "Play" to run continuously
        var playButton = Page.GetByRole(AriaRole.Button,
            new() { Name = "Play", Exact = true });
        await playButton.ClickAsync();

        // Let it run for 2 seconds
        await Task.Delay(2_000);

        // Click "Pause" to stop continuous execution
        var pauseButton = Page.GetByRole(AriaRole.Button,
            new() { Name = "Pause", Exact = true });
        await pauseButton.ClickAsync();
        await Task.Delay(500);

        // Verify the Session Metrics panel is visible and has data
        await Page.GetByText("Session Metrics").WaitForAsync(new() { Timeout = 5_000 });

        // Verify some metric values are displayed
        var bodyText = await Page.TextContentAsync("body");
        Assert.Contains("Sequence", bodyText, StringComparison.OrdinalIgnoreCase);

        // Click "Stop" to end the session
        var stopButton = Page.GetByRole(AriaRole.Button,
            new() { Name = "Stop", Exact = true });
        await stopButton.ClickAsync();

        // Wait for the session to return to idle — config editor should reappear
        await Page.GetByRole(AriaRole.Button, new() { Name = "Start Debug Session" })
            .WaitForAsync(new() { Timeout = 10_000 });

        AssertNoConsoleErrors();
        AssertNoNetworkFailures();
    }
}
