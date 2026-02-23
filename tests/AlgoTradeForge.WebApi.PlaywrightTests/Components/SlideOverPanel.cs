using Microsoft.Playwright;

namespace AlgoTradeForge.WebApi.PlaywrightTests.Components;

/// <summary>
/// Wraps SlideOver dialog interaction (close + wait for hidden).
/// </summary>
public sealed class SlideOverPanel(IPage page)
{
    public async Task CloseAsync()
    {
        await page.GetByLabel("Close panel").ClickAsync();
    }

    public async Task WaitForHiddenAsync(int timeout = 5_000)
    {
        await page.Locator("[role='dialog']").WaitForAsync(new()
        {
            State = WaitForSelectorState.Hidden,
            Timeout = timeout,
        });
    }
}
