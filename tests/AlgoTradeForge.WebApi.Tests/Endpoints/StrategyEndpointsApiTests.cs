using System.Net;
using System.Net.Http.Json;
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
}
