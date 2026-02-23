using Microsoft.Playwright;

namespace AlgoTradeForge.WebApi.PlaywrightTests.Components;

/// <summary>
/// Wraps debug toolbar button clicks.
/// </summary>
public sealed class DebugToolbar(IPage page)
{
    public Task ClickNextAsync() =>
        page.GetByRole(AriaRole.Button, new() { Name = "Next", Exact = true }).ClickAsync();

    public Task ClickToNextBarAsync() =>
        page.GetByRole(AriaRole.Button, new() { Name = "To Next Bar", Exact = true }).ClickAsync();

    public Task ClickPlayAsync() =>
        page.GetByRole(AriaRole.Button, new() { Name = "Play", Exact = true }).ClickAsync();

    public Task ClickPauseAsync() =>
        page.GetByRole(AriaRole.Button, new() { Name = "Pause", Exact = true }).ClickAsync();

    public Task ClickStopAsync() =>
        page.GetByRole(AriaRole.Button, new() { Name = "Stop", Exact = true }).ClickAsync();
}
