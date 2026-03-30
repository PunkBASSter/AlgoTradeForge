using AlgoTradeForge.WebApi.PlaywrightTests.Infrastructure;
using AlgoTradeForge.WebApi.PlaywrightTests.Pages;

namespace AlgoTradeForge.WebApi.PlaywrightTests.Tests;

[Collection("E2E")]
public sealed class ValidationE2eTests(PlaywrightFixture fixture) : PlaywrightTestBase(fixture)
{
    [Fact]
    public async Task ValidationTab_NavigatesAndRendersEmptyList()
    {
        var dashboard = new DashboardPage(Page, BaseUrl);
        await dashboard.NavigateAsync();

        // Click the Validation tab in the NavBar
        await dashboard.SelectTabAsync("Validation");

        // Should navigate to the validation list page
        await Page.WaitForURLAsync("**/all/validation", new() { Timeout = 10_000 });

        // Page heading should render
        await Page.GetByText("Validation Runs")
            .WaitForAsync(new() { Timeout = 10_000 });

        // Empty state should render (no validation runs in test DB)
        await Page.GetByText("No validation runs found.")
            .WaitForAsync(new() { Timeout = 10_000 });

        AssertNoConsoleErrors();
        AssertNoNetworkFailures();
    }
}
