using System.Net.Http.Json;
using System.Text.Json;
using AlgoTradeForge.WebApi.Contracts;

namespace AlgoTradeForge.WebApi.Tests.Infrastructure;

[CollectionDefinition("Api")]
public sealed class ApiTestCollection : ICollectionFixture<AlgoTradeForgeApiFactory>;

public abstract class ApiTestBase : IDisposable
{
    protected static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

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
        var body = (await response.Content.ReadFromJsonAsync<OptimizationSubmissionResponse>(Json))!;
        return (response, body);
    }

    protected async Task<OptimizationStatusResponse> PollOptimizationUntilDoneAsync(Guid id, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var response = await Client.GetFromJsonAsync<OptimizationStatusResponse>(
                $"/api/optimizations/{id}/status", Json);

            if (response!.Result is not null)
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
        AssetName = "BTCUSDT",
        Exchange = "Binance",
        StrategyName = strategyName ?? "ZigZagBreakout",
        InitialCash = 10_000m,
        StartTime = startTime ?? new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
        EndTime = endTime ?? new DateTimeOffset(2025, 1, 15, 0, 0, 0, TimeSpan.Zero),
        TimeFrame = timeFrame ?? "01:00:00",
    };

    protected static StartDebugSessionRequest MakeDebugSessionRequest() => new()
    {
        AssetName = "BTCUSDT",
        Exchange = "Binance",
        StrategyName = "ZigZagBreakout",
        InitialCash = 10_000m,
        StartTime = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
        EndTime = new DateTimeOffset(2025, 1, 5, 0, 0, 0, TimeSpan.Zero),
        TimeFrame = "01:00:00",
    };

    public void Dispose() => Client.Dispose();
}
