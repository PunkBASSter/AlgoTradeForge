using System.Net;
using System.Net.Http.Json;
using AlgoTradeForge.WebApi.Contracts;
using AlgoTradeForge.WebApi.Tests.Infrastructure;

namespace AlgoTradeForge.WebApi.Tests.Endpoints;

[Collection("Api")]
public sealed class StrategyEndpointsApiTests(AlgoTradeForgeApiFactory factory) : ApiTestBase(factory)
{
    [Fact]
    public async Task GetStrategies_Returns200()
    {
        var response = await Client.GetAsync("/api/strategies");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var names = await response.Content.ReadFromJsonAsync<List<string>>(Json);
        Assert.NotNull(names);
    }

    [Fact]
    public async Task GetStrategies_AfterBacktestCompletes_ContainsStrategyName()
    {
        // Run a backtest to populate history
        var request = MakeBacktestRequest();
        var (_, submission) = await SubmitBacktestAsync(request);
        await PollBacktestUntilDoneAsync(submission.Id, TimeSpan.FromSeconds(60));

        var response = await Client.GetAsync("/api/strategies");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var names = await response.Content.ReadFromJsonAsync<List<string>>(Json);
        Assert.NotNull(names);
        Assert.Contains("ZigZagBreakout", names);
    }

    [Fact]
    public async Task GetAvailableStrategies_Returns200()
    {
        var response = await Client.GetAsync("/api/strategies/available");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var strategies = await response.Content.ReadFromJsonAsync<List<StrategyDescriptorResponse>>(Json);
        Assert.NotNull(strategies);
    }

    [Fact]
    public async Task GetAvailableStrategies_ContainsZigZagBreakout()
    {
        var response = await Client.GetAsync("/api/strategies/available");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var strategies = await response.Content.ReadFromJsonAsync<List<StrategyDescriptorResponse>>(Json);
        Assert.NotNull(strategies);
        Assert.Contains(strategies, s => s.Name == "ZigZagBreakout");
    }

    [Fact]
    public async Task GetAvailableStrategies_ReturnsDefaultsAndAxes()
    {
        var response = await Client.GetAsync("/api/strategies/available");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var strategies = await response.Content.ReadFromJsonAsync<List<StrategyDescriptorResponse>>(Json);
        Assert.NotNull(strategies);

        var zigzag = strategies.Single(s => s.Name == "ZigZagBreakout");

        // Verify defaults contain DzzDepth
        Assert.True(zigzag.ParameterDefaults.ContainsKey("DzzDepth"));

        // Verify 3 optimization axes
        Assert.Equal(3, zigzag.OptimizationAxes.Count);
        Assert.Contains(zigzag.OptimizationAxes, a => a.Name == "DzzDepth" && a.Type == "numeric");
        Assert.Contains(zigzag.OptimizationAxes, a => a.Name == "MinimumThreshold" && a.Type == "numeric");
        Assert.Contains(zigzag.OptimizationAxes, a => a.Name == "RiskPercentPerTrade" && a.Type == "numeric");
    }
}
