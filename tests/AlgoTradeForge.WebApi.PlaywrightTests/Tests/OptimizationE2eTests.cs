using AlgoTradeForge.WebApi.PlaywrightTests.Infrastructure;
using Microsoft.Playwright;

namespace AlgoTradeForge.WebApi.PlaywrightTests.Tests;

[Collection("E2E")]
public sealed class OptimizationE2eTests(PlaywrightFixture fixture) : PlaywrightTestBase(fixture)
{
    // 8 combinations: DzzDepth(4,6) x MinimumThreshold(5000,10000) x RiskPercentPerTrade(0.5,1.0)
    private const string OptimizationConfig = """
        {
          "strategyName": "ZigZagBreakout",
          "optimizationAxes": {
            "DzzDepth": { "min": 4, "max": 6, "step": 2 },
            "MinimumThreshold": { "min": 5000, "max": 10000, "step": 5000 },
            "RiskPercentPerTrade": { "min": 0.5, "max": 1, "step": 0.5 }
          },
          "dataSubscriptions": [
            { "asset": "BTCUSDT", "exchange": "Binance", "timeFrame": "01:00:00" }
          ],
          "initialCash": 10000,
          "startTime": "2025-01-01T00:00:00Z",
          "endTime": "2025-01-15T00:00:00Z",
          "commissionPerTrade": 0.001,
          "slippageTicks": 2,
          "sortBy": "sortinoRatio"
        }
        """;

    [Fact]
    public async Task FullOptimizationLifecycle_SubmitWaitAndViewReport()
    {
        // Navigate to dashboard
        await NavigateAndWaitAsync("/dashboard");

        // Switch to the Optimization tab
        var optimizationTab = Page.GetByRole(AriaRole.Tab, new() { Name = "Optimization" });
        await optimizationTab.ClickAsync();

        // Click "+ Run New" to open the slide-over panel
        var runNewButton = Page.GetByRole(AriaRole.Button, new() { Name = "Run New" });
        await runNewButton.ClickAsync();

        // Wait for the CodeMirror editor to appear
        await Page.Locator(".cm-editor").WaitForAsync(new() { Timeout = 10_000 });

        // Set the optimization config JSON
        await SetCodeMirrorContentAsync(OptimizationConfig);

        // Click "Run" to submit
        var runButton = Page.GetByRole(AriaRole.Button, new() { Name = "Run" }).Last;
        await runButton.ClickAsync();

        // Wait for the "Completed" badge (180s for optimization with 8 combos)
        var completedBadge = Page.GetByText("Completed");
        await completedBadge.WaitForAsync(new() { Timeout = 180_000 });

        // Close the slide-over panel
        var closeButton = Page.Locator("[role='dialog'] button").First;
        await closeButton.ClickAsync();

        // Wait for the panel to close
        await Page.Locator("[role='dialog']").WaitForAsync(new()
        {
            State = WaitForSelectorState.Hidden,
            Timeout = 5_000,
        });

        // Verify a row appeared in the optimization table
        var tableRow = Page.Locator("table tbody tr").First;
        await tableRow.WaitForAsync(new() { Timeout = 10_000 });

        // Click the row to navigate to the optimization report
        await tableRow.ClickAsync();

        // Wait for the optimization report page to load
        await Page.WaitForURLAsync("**/report/optimization/**", new() { Timeout = 10_000 });

        // Verify the summary info — "Total Combinations" with value 8
        var combinationsText = Page.GetByText("Total Combinations");
        await combinationsText.WaitForAsync(new() { Timeout = 10_000 });

        // Verify the "Trials" section is present
        var trialsSection = Page.GetByText("Trials", new() { Exact = false });
        await trialsSection.First.WaitForAsync(new() { Timeout = 10_000 });

        // Verify trial rows appear in the table (should be 8 or thereabouts)
        var trialRows = Page.Locator("table tbody tr");
        var rowCount = await trialRows.CountAsync();
        Assert.True(rowCount >= 1, $"Expected at least 1 trial row, found {rowCount}");

        AssertNoConsoleErrors();
        AssertNoNetworkFailures();
    }
}
