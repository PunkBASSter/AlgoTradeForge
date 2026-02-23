using AlgoTradeForge.WebApi.PlaywrightTests.Infrastructure;
using Microsoft.Playwright;

namespace AlgoTradeForge.WebApi.PlaywrightTests.Tests;

[Collection("E2E")]
public sealed class BacktestE2eTests(PlaywrightFixture fixture) : PlaywrightTestBase(fixture)
{
    private const string BacktestConfig = """
        {
          "assetName": "BTCUSDT",
          "exchange": "Binance",
          "strategyName": "ZigZagBreakout",
          "initialCash": 10000,
          "startTime": "2025-01-01T00:00:00Z",
          "endTime": "2025-01-15T00:00:00Z",
          "timeFrame": "01:00:00"
        }
        """;

    [Fact]
    public async Task FullBacktestLifecycle_SubmitWaitAndViewReport()
    {
        // Navigate to dashboard
        await NavigateAndWaitAsync("/dashboard");

        // Ensure we're on the Backtest tab
        var backtestTab = Page.GetByRole(AriaRole.Tab, new() { Name = "Backtest" });
        await backtestTab.ClickAsync();

        // Click "+ Run New" to open the slide-over panel
        var runNewButton = Page.GetByRole(AriaRole.Button, new() { Name = "Run New" });
        await runNewButton.ClickAsync();

        // Wait for the CodeMirror editor to appear
        await Page.Locator(".cm-editor").WaitForAsync(new() { Timeout = 10_000 });

        // Set the backtest config JSON
        await SetCodeMirrorContentAsync(BacktestConfig);

        // Click "Run" to submit
        var runButton = Page.GetByRole(AriaRole.Button, new() { Name = "Run" }).Last;
        await runButton.ClickAsync();

        // Wait for the "Completed" badge to appear (120s timeout for backtest execution)
        await Page.GetByText("Completed").WaitForAsync(new() { Timeout = 120_000 });

        // Close the slide-over panel
        var closeButton = Page.Locator("[role='dialog'] button").First;
        await closeButton.ClickAsync();

        // Wait for the panel to close
        await Page.Locator("[role='dialog']").WaitForAsync(new()
        {
            State = WaitForSelectorState.Hidden,
            Timeout = 5_000,
        });

        // Verify a row appeared in the runs table
        var tableRow = Page.Locator("table tbody tr").First;
        await tableRow.WaitForAsync(new() { Timeout = 10_000 });

        // Click the row to navigate to the report page
        await tableRow.ClickAsync();

        // Wait for the report page to load
        await Page.WaitForURLAsync("**/report/backtest/**", new() { Timeout = 10_000 });

        // Wait for metrics to load (async data fetch)
        await Page.GetByText("Total Trades").WaitForAsync(new() { Timeout = 15_000 });

        // Verify key metrics are displayed
        var metricsText = await Page.TextContentAsync("body");
        Assert.Contains("Total Trades", metricsText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Sharpe Ratio", metricsText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Net Profit", metricsText, StringComparison.OrdinalIgnoreCase);

        // Verify the equity curve section exists
        var equityCurve = Page.GetByText("Equity Curve", new() { Exact = false });
        await equityCurve.WaitForAsync(new() { Timeout = 10_000 });

        AssertNoConsoleErrors();
        AssertNoNetworkFailures();
    }
}
