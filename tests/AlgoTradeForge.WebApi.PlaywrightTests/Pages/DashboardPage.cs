using AlgoTradeForge.WebApi.PlaywrightTests.Components;
using Microsoft.Playwright;

namespace AlgoTradeForge.WebApi.PlaywrightTests.Pages;

/// <summary>
/// Encapsulates all dashboard interactions including the RunNewPanel workflow.
/// </summary>
public sealed class DashboardPage(IPage page, string baseUrl)
{
    public CodeMirrorEditor Editor { get; } = new(page);
    public SlideOverPanel Panel { get; } = new(page);

    public async Task NavigateAsync()
    {
        await page.GotoAsync(baseUrl + "/dashboard", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle,
            Timeout = 30_000,
        });
    }

    public async Task SelectTabAsync(string tabName)
    {
        await page.GetByRole(AriaRole.Tab, new() { Name = tabName }).ClickAsync();
    }

    public async Task OpenRunNewPanelAsync()
    {
        await page.GetByRole(AriaRole.Button, new() { Name = "Run New" }).ClickAsync();
    }

    public async Task SubmitRunAsync()
    {
        await page.GetByTestId("submit-run").ClickAsync();
    }

    public async Task WaitForRunCompletedAsync(int timeout = 120_000)
    {
        await page.GetByTestId("status-badge")
            .Filter(new() { HasText = "Completed" })
            .WaitForAsync(new() { Timeout = timeout });
    }

    public async Task WaitForRunTableRowAsync(int timeout = 10_000)
    {
        await page.GetByTestId("runs-table").Locator("tbody tr").First
            .WaitForAsync(new() { Timeout = timeout });
    }

    public async Task ClickFirstRunRowAsync()
    {
        await page.GetByTestId("runs-table").Locator("tbody tr").First.ClickAsync();
    }
}
