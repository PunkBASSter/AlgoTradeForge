using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlgoTradeForge.Application.Optimization;
using AlgoTradeForge.Infrastructure.Tests.Live.Testnet;
using AlgoTradeForge.WebApi.Contracts;

namespace AlgoTradeForge.WebApi.Tests.Live.Testnet;

[Collection("BinanceTestnetE2E")]
[Trait("Category", "BinanceTestnet")]
public sealed class TestnetE2ETests(TestnetApiFactory factory) : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _client = factory.CreateClient();

    [Fact(
#if DEBUG
        Skip = "Requires responsive Binance testnet — run in Release for full integration"
#endif
    )]
    public async Task FullHttpLifecycle_StartStreamOrderStop()
    {
        if (!BinanceTestnetCredentials.IsConfigured)
            Assert.Skip(BinanceTestnetCredentials.SkipReason);

        var strategy = factory.SharedStrategy;

        // 1. Start session
        var request = new StartLiveSessionRequest
        {
            StrategyName = "TestnetE2E",
            AccountName = "paper",
            InitialCash = 100m,
            DataSubscriptions =
            [
                new DataSubscriptionDto { Asset = "BTCUSDT", Exchange = "Binance", TimeFrame = "00:01:00" },
                new DataSubscriptionDto { Asset = "ETHUSDT", Exchange = "Binance", TimeFrame = "00:01:00" },
            ],
            EnabledEvents = ["OnBarComplete", "OnTrade"],
        };

        var startResponse = await _client.PostAsJsonAsync("/api/live/sessions", request, Json, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, startResponse.StatusCode);

        var startBody = await startResponse.Content.ReadFromJsonAsync<LiveSessionSubmissionResponse>(Json, TestContext.Current.CancellationToken);
        Assert.NotNull(startBody);
        var sessionId = startBody!.SessionId;
        Assert.NotEqual(Guid.Empty, sessionId);

        // 2. Check status
        var statusResponse = await _client.GetFromJsonAsync<LiveSessionStatusResponse>(
            $"/api/live/sessions/{sessionId}", Json, TestContext.Current.CancellationToken);
        Assert.NotNull(statusResponse);
        Assert.Equal("Running", statusResponse!.Status);

        // 3. List sessions
        var listResponse = await _client.GetFromJsonAsync<LiveSessionListResponse>(
            "/api/live/sessions", Json, TestContext.Current.CancellationToken);
        Assert.NotNull(listResponse);
        Assert.Contains(listResponse!.Sessions, s => s.SessionId == sessionId);

        // 4. Await bars from both assets (up to 120s)
        await strategy.BothAssetsReceived.Task.WaitAsync(TimeSpan.FromSeconds(120), TestContext.Current.CancellationToken);
        var assetNames = strategy.BarsReceived.Select(b => b.AssetName).Distinct().ToList();
        Assert.Contains("BTCUSDT", assetNames);
        Assert.Contains("ETHUSDT", assetNames);

        // 5. Await market buy + sell cycle (up to 180s)
        await strategy.OrderTestsCompleted.Task.WaitAsync(TimeSpan.FromSeconds(180), TestContext.Current.CancellationToken);

        Assert.Null(strategy.TestException);
        Assert.True(strategy.FillsReceived.Count >= 2,
            $"Expected at least 2 fills (buy+sell), got {strategy.FillsReceived.Count}.");

        // 6. Stop session
        var stopResponse = await _client.DeleteAsync($"/api/live/sessions/{sessionId}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, stopResponse.StatusCode);

        // 7. Verify stopped — session should be removed from store
        var afterStop = await _client.GetAsync($"/api/live/sessions/{sessionId}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, afterStop.StatusCode);
    }

    public void Dispose() => _client.Dispose();
}

[CollectionDefinition("BinanceTestnetE2E")]
public sealed class BinanceTestnetE2ECollection : ICollectionFixture<TestnetApiFactory>;
