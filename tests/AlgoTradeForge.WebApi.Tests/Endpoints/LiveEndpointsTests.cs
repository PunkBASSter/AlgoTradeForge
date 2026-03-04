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
        var response = await _client.GetAsync("/api/live/sessions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<LiveSessionListResponse>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(result);
        Assert.Empty(result.Sessions);
    }

    [Fact]
    public async Task GetSession_NonExistent_Returns404()
    {
        var response = await _client.GetAsync($"/api/live/sessions/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task StopSession_NonExistent_Returns404()
    {
        var response = await _client.DeleteAsync($"/api/live/sessions/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task StartSession_InvalidAsset_Returns400()
    {
        var request = new StartLiveSessionRequest
        {
            AssetName = "NONEXISTENT",
            Exchange = "Binance",
            StrategyName = "BuyAndHold",
            InitialCash = 10000m,
            PaperTrading = true,
        };

        var response = await _client.PostAsJsonAsync("/api/live/sessions", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task StartSession_InvalidStrategy_Returns400()
    {
        var request = new StartLiveSessionRequest
        {
            AssetName = "BTCUSDT",
            Exchange = "Binance",
            StrategyName = "NonExistentStrategy",
            InitialCash = 10000m,
            PaperTrading = true,
        };

        var response = await _client.PostAsJsonAsync("/api/live/sessions", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task StartSession_MissingCredentials_ReturnsError()
    {
        // Without testnet API credentials configured, starting a paper session should fail
        var request = new StartLiveSessionRequest
        {
            AssetName = "BTCUSDT",
            Exchange = "Binance",
            StrategyName = "BuyAndHold",
            InitialCash = 10000m,
            PaperTrading = true,
            TimeFrame = "00:01:00",
            EnabledEvents = ["OnBarComplete", "OnTrade"],
        };

        var response = await _client.PostAsJsonAsync("/api/live/sessions", request);

        // No API credentials in test environment → server error
        Assert.False(response.IsSuccessStatusCode);
    }
}
