using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Subscriptions;
using System.Net.Http.Json;
using System.Text.Json;
using AlgoTradeForge.Application;
using AlgoTradeForge.WebApi.Contracts;

[assembly: TestCaseOrderer(typeof(Xunit.v3.DefaultTestCaseOrderer))]
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace AlgoTradeForge.WebApi.Tests.Infrastructure;

[CollectionDefinition("Api")]
public sealed class ApiTestCollection : ICollectionFixture<AlgoTradeForgeApiFactory>;

public abstract class ApiTestBase : IDisposable
{
    protected static readonly JsonSerializerOptions Json = JsonDefaults.Api;

    protected HttpClient Client { get; }

    protected ApiTestBase(AlgoTradeForgeApiFactory factory)
    {
        Client = factory.CreateClient();
    }

    protected async Task<(HttpResponseMessage Response, BacktestSubmissionResponse Body)> SubmitBacktestAsync(
        RunBacktestRequest request)
    {
        var response = await Client.PostAsJsonAsync("/api/backtests", request, Json);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException(
                $"POST /api/backtests returned {(int)response.StatusCode}: {errorBody}");
        }
        var body = (await response.Content.ReadFromJsonAsync<BacktestSubmissionResponse>(Json))!;
        return (response, body);
    }

    protected async Task<BacktestStatusResponse> PollBacktestUntilDoneAsync(Guid id, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var response = await Client.GetFromJsonAsync<BacktestStatusResponse>(
                $"/api/backtests/{id}/status", Json);

            if (response!.Result is not null)
                return response;

            await Task.Delay(500);
        }

        throw new TimeoutException($"Backtest {id} did not complete within {timeout}.");
    }

    protected async Task<(HttpResponseMessage Response, OptimizationSubmissionResponse Body)> SubmitOptimizationAsync(
        RunOptimizationRequest request)
    {
        var response = await Client.PostAsJsonAsync("/api/optimizations", request, Json);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException(
                $"POST /api/optimizations returned {(int)response.StatusCode}: {errorBody}");
        }

        // The endpoint returns OptimizationGroupSubmissionResponse when subscriptionAxis is present.
        // Try group response first, then fall back to single-run response.
        var content = await response.Content.ReadAsStringAsync();
        var doc = System.Text.Json.JsonDocument.Parse(content);
        if (doc.RootElement.TryGetProperty("groupId", out _))
        {
            var group = System.Text.Json.JsonSerializer.Deserialize<OptimizationGroupSubmissionResponse>(content, Json)!;
            var body = new OptimizationSubmissionResponse
            {
                Id = group.Runs.Count > 0 ? group.Runs[0].Id : group.GroupId,
                TotalCombinations = group.TotalCombinationsPerRun,
            };
            return (response, body);
        }

        var single = System.Text.Json.JsonSerializer.Deserialize<OptimizationSubmissionResponse>(content, Json)!;
        return (response, single);
    }

    protected async Task<OptimizationStatusResponse> PollOptimizationUntilDoneAsync(Guid id, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var response = await Client.GetFromJsonAsync<OptimizationStatusResponse>(
                $"/api/optimizations/{id}/status", Json);

            // Check for terminal status — Enqueued placeholders have Result != null
            // but are not yet processed by the compute queue consumer
            if (response!.Status is "Completed" or "Failed" or "Cancelled")
                return response;

            await Task.Delay(500);
        }

        throw new TimeoutException($"Optimization {id} did not complete within {timeout}.");
    }

    protected static RunBacktestRequest MakeBacktestRequest(
        string? strategyName = null,
        string? timeFrame = null,
        DateTimeOffset? startTime = null,
        DateTimeOffset? endTime = null) => new()
    {
        DataSubscriptions = [new TimeBarSubscription("BTCUSDT", "Binance", DataFeedRole.Primary, TimeFrame.TryParseLiberal(timeFrame ?? "1h", out var tf) ? tf : new TimeFrame(TimeSpan.FromHours(1)))],
        BacktestSettings = new()
        {
            InitialCash = 10_000m,
            StartTime = startTime ?? new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            EndTime = endTime ?? new DateTimeOffset(2025, 1, 15, 0, 0, 0, TimeSpan.Zero),
        },
        StrategyName = strategyName ?? "BuyAndHold",
    };

    protected static StartDebugSessionRequest MakeDebugSessionRequest() => new()
    {
        DataSubscriptions = [new TimeBarSubscription("BTCUSDT", "Binance", DataFeedRole.Primary, TimeFrame.Parse("1h"))],
        BacktestSettings = new()
        {
            InitialCash = 10_000m,
            StartTime = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            EndTime = new DateTimeOffset(2025, 1, 5, 0, 0, 0, TimeSpan.Zero),
        },
        StrategyName = "BuyAndHold",
    };

    public void Dispose() => Client.Dispose();
}
