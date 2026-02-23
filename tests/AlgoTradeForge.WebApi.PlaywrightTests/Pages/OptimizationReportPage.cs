using Microsoft.Playwright;

namespace AlgoTradeForge.WebApi.PlaywrightTests.Pages;

/// <summary>
/// Encapsulates optimization report page verification.
/// </summary>
public sealed class OptimizationReportPage(IPage page)
{
    public async Task WaitForLoadAsync(int timeout = 10_000)
    {
        await page.WaitForURLAsync("**/report/optimization/**", new() { Timeout = timeout });
        await page.GetByText("Total Combinations").WaitForAsync(new() { Timeout = timeout });
    }

    public async Task AssertHasTrialsSection(int timeout = 10_000)
    {
        await page.GetByText("Trials", new() { Exact = false }).First
            .WaitForAsync(new() { Timeout = timeout });
    }

    public async Task<int> GetTrialRowCountAsync()
    {
        return await page.GetByTestId("trials-table").Locator("tbody tr").CountAsync();
    }
}
