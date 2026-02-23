using AlgoTradeForge.WebApi.PlaywrightTests.Infrastructure;
using AlgoTradeForge.WebApi.PlaywrightTests.Pages;

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
        var dashboard = new DashboardPage(Page, BaseUrl);
        await dashboard.NavigateAsync();
        await dashboard.SelectTabAsync("Optimization");

        await dashboard.OpenRunNewPanelAsync();
        await dashboard.Editor.WaitForReadyAsync();
        await dashboard.Editor.SetContentAsync(OptimizationConfig);
        await dashboard.SubmitRunAsync();
        await dashboard.WaitForRunCompletedAsync(180_000);
        await dashboard.Panel.CloseAsync();
        await dashboard.Panel.WaitForHiddenAsync();

        await dashboard.WaitForRunTableRowAsync(10_000);
        await dashboard.ClickFirstRunRowAsync();

        var report = new OptimizationReportPage(Page);
        await report.WaitForLoadAsync(10_000);
        await report.AssertHasTrialsSection();

        var rowCount = await report.GetTrialRowCountAsync();
        Assert.True(rowCount >= 1, $"Expected at least 1 trial row, found {rowCount}");

        AssertNoConsoleErrors();
        AssertNoNetworkFailures();
    }
}
