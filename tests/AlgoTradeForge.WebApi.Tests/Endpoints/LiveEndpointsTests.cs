using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlgoTradeForge.WebApi.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace AlgoTradeForge.WebApi.Tests.Endpoints;

public class LiveEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public LiveEndpointsTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task ListSessions_Empty_ReturnsOk()
    {
        var response = await _client.GetAsync("/api/live/sessions", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var result = JsonSerializer.Deserialize<LiveSessionListResponse>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(result);
        Assert.Empty(result.Sessions);
    }

    [Fact]
    public async Task GetSession_NonExistent_Returns404()
    {
        var response = await _client.GetAsync($"/api/live/sessions/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task StopSession_NonExistent_Returns404()
    {
        var response = await _client.DeleteAsync($"/api/live/sessions/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task StartSession_InvalidStrategy_Returns400()
    {
        var request = new StartLiveSessionRequest
        {
            StrategyName = "NonExistentStrategy",
            InitialCash = 10000m,
            AccountName = "paper",
        };

        var response = await _client.PostAsJsonAsync("/api/live/sessions", request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task StartSession_NoSubscriptions_Returns400()
    {
        // BuyAndHold has no DataSubscriptions by default → should fail validation
        var request = new StartLiveSessionRequest
        {
            StrategyName = "BuyAndHold",
            InitialCash = 10000m,
            AccountName = "paper",
        };

        var response = await _client.PostAsJsonAsync("/api/live/sessions", request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
