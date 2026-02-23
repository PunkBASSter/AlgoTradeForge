using AlgoTradeForge.WebApi.PlaywrightTests.Infrastructure;
using AlgoTradeForge.WebApi.PlaywrightTests.Pages;

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
        var dashboard = new DashboardPage(Page, BaseUrl);
        await dashboard.NavigateAsync();
        await dashboard.SelectTabAsync("Backtest");

        await dashboard.OpenRunNewPanelAsync();
        await dashboard.Editor.WaitForReadyAsync();
        await dashboard.Editor.SetContentAsync(BacktestConfig);
        await dashboard.SubmitRunAsync();
        await dashboard.WaitForRunCompletedAsync(120_000);
        await dashboard.Panel.CloseAsync();
        await dashboard.Panel.WaitForHiddenAsync();

        await dashboard.WaitForRunTableRowAsync(10_000);
        await dashboard.ClickFirstRunRowAsync();

        var report = new BacktestReportPage(Page);
        await report.WaitForLoadAsync(15_000);
        await report.AssertHasMetric("Total Trades");
        await report.AssertHasMetric("Sharpe Ratio");
        await report.AssertHasMetric("Net Profit");
        await report.AssertHasSection("Equity Curve");

        AssertNoConsoleErrors();
        AssertNoNetworkFailures();
    }
}
