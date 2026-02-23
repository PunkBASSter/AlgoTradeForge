using AlgoTradeForge.WebApi.PlaywrightTests.Infrastructure;
using AlgoTradeForge.WebApi.PlaywrightTests.Pages;

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
        var debug = new DebugPage(Page, BaseUrl);
        await debug.NavigateAsync();

        await debug.Editor.WaitForReadyAsync();
        await debug.Editor.SetContentAsync(DebugConfig);
        await debug.StartSessionAsync();
        await debug.WaitForToolbarAsync(30_000);

        await debug.Toolbar.ClickNextAsync();
        await Task.Delay(500);
        await debug.Toolbar.ClickToNextBarAsync();
        await Task.Delay(500);
        await debug.Toolbar.ClickPlayAsync();
        await Task.Delay(2_000);
        await debug.Toolbar.ClickPauseAsync();
        await Task.Delay(500);

        await debug.WaitForMetricsVisibleAsync();
        await debug.AssertHasMetricText("Sequence");

        await debug.Toolbar.ClickStopAsync();
        await debug.WaitForIdleAsync(10_000);

        AssertNoConsoleErrors();
        AssertNoNetworkFailures();
    }
}
