using Microsoft.Playwright;

namespace AlgoTradeForge.WebApi.PlaywrightTests.Pages;

/// <summary>
/// Encapsulates backtest report page verification.
/// </summary>
public sealed class BacktestReportPage(IPage page)
{
    public async Task WaitForLoadAsync(int timeout = 15_000)
    {
        await page.WaitForURLAsync("**/report/backtest/**", new() { Timeout = timeout });
        await page.GetByText("Total Trades").WaitForAsync(new() { Timeout = timeout });
    }

    public async Task AssertHasMetric(string metricName)
    {
        var bodyText = await page.TextContentAsync("body");
        Assert.Contains(metricName, bodyText, StringComparison.OrdinalIgnoreCase);
    }

    public async Task AssertHasSection(string sectionName)
    {
        await page.GetByText(sectionName, new() { Exact = false })
            .WaitForAsync(new() { Timeout = 10_000 });
    }
}
