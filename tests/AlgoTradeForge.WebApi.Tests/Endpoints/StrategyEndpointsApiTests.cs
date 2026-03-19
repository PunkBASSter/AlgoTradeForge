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
        var response = await Client.GetAsync("/api/strategies", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var names = await response.Content.ReadFromJsonAsync<List<string>>(Json, TestContext.Current.CancellationToken);
        Assert.NotNull(names);
    }

    [Fact]
    public async Task GetStrategies_AfterBacktestCompletes_ContainsStrategyName()
    {
        // Run a backtest to populate history
        var request = MakeBacktestRequest();
        var (_, submission) = await SubmitBacktestAsync(request);
        await PollBacktestUntilDoneAsync(submission.Id, TimeSpan.FromSeconds(60));

        var response = await Client.GetAsync("/api/strategies", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var names = await response.Content.ReadFromJsonAsync<List<string>>(Json, TestContext.Current.CancellationToken);
        Assert.NotNull(names);
        Assert.Contains("BuyAndHold", names);
    }

    [Fact]
    public async Task GetAvailableStrategies_Returns200()
    {
        var response = await Client.GetAsync("/api/strategies/available", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var strategies = await response.Content.ReadFromJsonAsync<List<StrategyDescriptorResponse>>(Json, TestContext.Current.CancellationToken);
        Assert.NotNull(strategies);
    }

    [Fact]
    public async Task GetAvailableStrategies_ContainsBuyAndHold()
    {
        var response = await Client.GetAsync("/api/strategies/available", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var strategies = await response.Content.ReadFromJsonAsync<List<StrategyDescriptorResponse>>(Json, TestContext.Current.CancellationToken);
        Assert.NotNull(strategies);
        Assert.Contains(strategies, s => s.Name == "BuyAndHold");
    }

    [Fact]
    public async Task GetAvailableStrategies_ReturnsDefaultsAndAxes()
    {
        var response = await Client.GetAsync("/api/strategies/available", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var strategies = await response.Content.ReadFromJsonAsync<List<StrategyDescriptorResponse>>(Json, TestContext.Current.CancellationToken);
        Assert.NotNull(strategies);

        var buyAndHold = strategies.Single(s => s.Name == "BuyAndHold");

        // Verify defaults contain Quantity
        Assert.True(buyAndHold.ParameterDefaults.ContainsKey("Quantity"));

        // Verify 1 optimization axis
        Assert.Single(buyAndHold.OptimizationAxes);
        Assert.Contains(buyAndHold.OptimizationAxes, a => a.Name == "Quantity" && a.Type == "numeric");
    }
}
