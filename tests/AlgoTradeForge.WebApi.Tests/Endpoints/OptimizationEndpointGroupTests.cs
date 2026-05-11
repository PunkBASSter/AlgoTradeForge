using System.Net;
using System.Net.Http.Json;
using AlgoTradeForge.Application;
using AlgoTradeForge.Application.Optimization;
using AlgoTradeForge.WebApi.Contracts;
using AlgoTradeForge.WebApi.Tests.Infrastructure;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Subscriptions;

namespace AlgoTradeForge.WebApi.Tests.Endpoints;

/// <summary>
/// Integration tests for optimization group endpoints (T032, T033, T043, T051).
/// Covers: group submission, detail, status, trials (with params/sortBy), cross-DSS trials,
/// cancel, delete, and evaluate with dssCount.
/// </summary>
[Collection("Api")]
public sealed class OptimizationEndpointGroupTests(AlgoTradeForgeApiFactory factory) : ApiTestBase(factory)
{
    // Each call returns a unique request to avoid RunKey dedup collisions between tests
    private static int _requestCounter;
    private static RunOptimizationRequest MakeGroupOptimizationRequest()
    {
        var offset = Interlocked.Increment(ref _requestCounter) * 10m;
        return new()
        {
            StrategyName = "BuyAndHold",
            BacktestSettings = new()
            {
                InitialCash = 10_000m + offset,
                StartTime = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
                EndTime = new DateTimeOffset(2025, 1, 15, 0, 0, 0, TimeSpan.Zero),
            },
            OptimizationSettings = new()
            {
                MaxDegreeOfParallelism = 1,
                MinTradeCount = null,
            },
            SubscriptionAxis =
            [
                [new TimeBarSubscription("BTCUSDT", "Binance", DataFeedRole.Primary, TimeFrame.Parse("1h"))]
            ],
            OptimizationAxes = new Dictionary<string, OptimizationAxisOverride>
            {
                ["Quantity"] = new RangeOverride(1m, 3m, 2m), // 2 values: 1, 3
            },
        };
    }

    private async Task<OptimizationGroupSubmissionResponse> SubmitGroupOptimizationAsync(
        RunOptimizationRequest request)
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await Client.PostAsJsonAsync("/api/optimizations", request, Json, ct);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"POST /api/optimizations returned {(int)response.StatusCode}: {errorBody}");
        }
        return (await response.Content.ReadFromJsonAsync<OptimizationGroupSubmissionResponse>(Json, ct))!;
    }

    private async Task<OptimizationGroupSubmissionResponse> SubmitAndWaitForGroupCompletionAsync(
        RunOptimizationRequest? request = null, TimeSpan? timeout = null)
    {
        request ??= MakeGroupOptimizationRequest();
        var submission = await SubmitGroupOptimizationAsync(request);

        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(120);
        foreach (var run in submission.Runs)
            await PollRunUntilTerminalAsync(run.Id, effectiveTimeout);

        return submission;
    }

    /// <summary>
    /// Polls the run status until it reaches a terminal state (Completed/Failed/Cancelled).
    /// Unlike the base class poll which only checks Result is not null, this also checks
    /// the Status field — necessary because the compute queue saves Enqueued placeholders.
    /// </summary>
    private async Task PollRunUntilTerminalAsync(Guid id, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var response = await Client.GetFromJsonAsync<OptimizationStatusResponse>(
                $"/api/optimizations/{id}/status", Json, TestContext.Current.CancellationToken);

            if (response?.Status is "Completed" or "Failed" or "Cancelled")
                return;

            await Task.Delay(500, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"Optimization {id} did not reach terminal state within {timeout}.");
    }

    // ── T032: POST creates group, response contains groupId + runs array ──

    [Fact]
    public async Task Post_WithSubscriptionAxis_ReturnsGroupSubmission()
    {
        var ct = TestContext.Current.CancellationToken;
        var request = MakeGroupOptimizationRequest();

        var response = await Client.PostAsJsonAsync("/api/optimizations", request, Json, ct);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<OptimizationGroupSubmissionResponse>(Json, ct);
        Assert.NotNull(body);
        Assert.NotEqual(Guid.Empty, body.GroupId);
        Assert.Single(body.Runs);
        Assert.True(body.TotalCombinationsPerRun > 0);
        Assert.NotEqual(Guid.Empty, body.Runs[0].Id);
    }

    [Fact]
    public async Task Evaluate_WithSubscriptionAxis_ReturnsDssCount()
    {
        var ct = TestContext.Current.CancellationToken;
        var request = new EvaluateOptimizationRequest
        {
            StrategyName = "BuyAndHold",
            OptimizationAxes = new Dictionary<string, OptimizationAxisOverride>
            {
                ["Quantity"] = new RangeOverride(1m, 3m, 2m),
            },
            SubscriptionAxis =
            [
                [new TimeBarSubscription("BTCUSDT", "Binance", DataFeedRole.Primary, TimeFrame.Parse("1h"))]
            ],
        };

        var response = await Client.PostAsJsonAsync("/api/optimizations/evaluate", request, Json, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<OptimizationEvaluationResponse>(Json, ct);
        Assert.NotNull(body);
        Assert.Equal(1, body.DataSubscriptionSetsCount);
        Assert.True(body.TotalCombinations > 0);
    }

    // ── T033: Group management endpoints ─────────────────────────────────

    [Fact]
    public async Task GetGroupDetail_AfterCompletion_ReturnsChildRuns()
    {
        var ct = TestContext.Current.CancellationToken;
        var submission = await SubmitAndWaitForGroupCompletionAsync();

        var response = await Client.GetAsync(
            $"/api/optimizations/groups/{submission.GroupId}", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var detail = await response.Content.ReadFromJsonAsync<OptimizationGroupDetailResponse>(Json, ct);
        Assert.NotNull(detail);
        Assert.Equal(submission.GroupId, detail.Id);
        Assert.Equal("BuyAndHold", detail.StrategyName);
        Assert.Equal("BruteForce", detail.OptimizationMethod);
        Assert.Single(detail.Runs);
        Assert.Equal("Completed", detail.Runs[0].Status);
        Assert.True(detail.Runs[0].TotalCombinations > 0);
        Assert.NotEmpty(detail.Subscriptions);
    }

    [Fact]
    public async Task GetGroupStatus_AfterCompletion_ShowsAllRunsCompleted()
    {
        var ct = TestContext.Current.CancellationToken;
        var submission = await SubmitAndWaitForGroupCompletionAsync();

        var response = await Client.GetAsync(
            $"/api/optimizations/groups/{submission.GroupId}/status", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var status = await response.Content.ReadFromJsonAsync<OptimizationGroupStatusResponse>(Json, ct);
        Assert.NotNull(status);
        Assert.Equal(submission.GroupId, status.Id);
        Assert.Single(status.Runs);
        Assert.Equal("Completed", status.Runs[0].Status);
        Assert.True(status.Runs[0].Processed > 0);
        Assert.Equal(status.Runs[0].Total, status.Runs[0].Processed);
    }

    [Fact]
    public async Task DeleteGroup_CascadesAndReturns204()
    {
        var ct = TestContext.Current.CancellationToken;
        var submission = await SubmitAndWaitForGroupCompletionAsync();

        var deleteResponse = await Client.DeleteAsync(
            $"/api/optimizations/groups/{submission.GroupId}", ct);
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        // Group should be gone
        var getResponse = await Client.GetAsync(
            $"/api/optimizations/groups/{submission.GroupId}", ct);
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);

        // Child run should also be gone
        var runResponse = await Client.GetAsync(
            $"/api/optimizations/{submission.Runs[0].Id}", ct);
        Assert.Equal(HttpStatusCode.NotFound, runResponse.StatusCode);
    }

    [Fact]
    public async Task CancelGroup_ReturnsNoContentOr404()
    {
        var ct = TestContext.Current.CancellationToken;
        // Submit with many combinations so it's likely still running when we cancel
        var request = new RunOptimizationRequest
        {
            StrategyName = "BuyAndHold",
            BacktestSettings = new()
            {
                InitialCash = 10_000m,
                StartTime = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
                EndTime = new DateTimeOffset(2025, 4, 1, 0, 0, 0, TimeSpan.Zero),
            },
            OptimizationSettings = new()
            {
                MaxDegreeOfParallelism = 1,
                MinTradeCount = null,
            },
            SubscriptionAxis =
            [
                [new TimeBarSubscription("BTCUSDT", "Binance", DataFeedRole.Primary, TimeFrame.Parse("1h"))]
            ],
            OptimizationAxes = new Dictionary<string, OptimizationAxisOverride>
            {
                ["Quantity"] = new RangeOverride(0.1m, 10m, 0.1m),
            },
        };

        var submission = await SubmitGroupOptimizationAsync(request);

        var response = await Client.PostAsync(
            $"/api/optimizations/groups/{submission.GroupId}/cancel", null, ct);

        // Accept 204 (cancelled) or 404 (already completed before cancel reached it)
        Assert.True(
            response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound,
            $"Expected 204 or 404, got {(int)response.StatusCode}");
    }

    // ── 404 for non-existent groups ─────────────────────────────────────

    [Fact]
    public async Task GetGroup_RandomGuid_Returns404()
    {
        var response = await Client.GetAsync(
            $"/api/optimizations/groups/{Guid.NewGuid()}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetGroupStatus_RandomGuid_Returns404()
    {
        var response = await Client.GetAsync(
            $"/api/optimizations/groups/{Guid.NewGuid()}/status", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CancelGroup_RandomGuid_Returns404()
    {
        var response = await Client.PostAsync(
            $"/api/optimizations/groups/{Guid.NewGuid()}/cancel", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteGroup_RandomGuid_Returns404()
    {
        var response = await Client.DeleteAsync(
            $"/api/optimizations/groups/{Guid.NewGuid()}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── T043: Trials endpoint with params and sortBy ─────────────────────

    [Fact]
    public async Task GetTrials_AfterCompletion_IncludesParamsField()
    {
        var ct = TestContext.Current.CancellationToken;
        var submission = await SubmitAndWaitForGroupCompletionAsync();

        var response = await Client.GetAsync(
            $"/api/optimizations/{submission.Runs[0].Id}/trials", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var paged = await response.Content.ReadFromJsonAsync<PagedResponse<BacktestRunResponse>>(Json, ct);
        Assert.NotNull(paged);
        Assert.NotEmpty(paged.Items);

        // Every trial should have a Params string (BuyAndHold has at least Quantity)
        foreach (var trial in paged.Items)
        {
            Assert.NotNull(trial.Params);
            Assert.Contains("Quantity", trial.Params);
        }
    }

    [Theory]
    [InlineData("SharpeRatio")]
    [InlineData("SortinoRatio")]
    [InlineData("ProfitFactor")]
    [InlineData("MaxDrawdownPct")]
    [InlineData("TotalTrades")]
    [InlineData("NetProfit")]
    [InlineData("FitnessScore")]
    public async Task GetTrials_WithSortBy_ReturnsOk(string sortBy)
    {
        var ct = TestContext.Current.CancellationToken;
        var submission = await SubmitAndWaitForGroupCompletionAsync();

        var response = await Client.GetAsync(
            $"/api/optimizations/{submission.Runs[0].Id}/trials?sortBy={sortBy}", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var paged = await response.Content.ReadFromJsonAsync<PagedResponse<BacktestRunResponse>>(Json, ct);
        Assert.NotNull(paged);
        Assert.NotEmpty(paged.Items);
    }

    // ── T051: Cross-DSS trials endpoint ──────────────────────────────────

    [Fact]
    public async Task GetGroupTrials_AfterCompletion_ReturnsCrossDssTrials()
    {
        var ct = TestContext.Current.CancellationToken;
        var submission = await SubmitAndWaitForGroupCompletionAsync();

        var response = await Client.GetAsync(
            $"/api/optimizations/groups/{submission.GroupId}/trials", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var paged = await response.Content.ReadFromJsonAsync<PagedResponse<BacktestRunResponse>>(Json, ct);
        Assert.NotNull(paged);
        Assert.NotEmpty(paged.Items);

        // Each trial should reference an optimization run
        foreach (var trial in paged.Items)
        {
            Assert.NotNull(trial.OptimizationRunId);
            Assert.Contains(submission.Runs, r => r.Id == trial.OptimizationRunId);
        }
    }

    [Fact]
    public async Task GetGroupTrials_WithSortBy_ReturnsOk()
    {
        var ct = TestContext.Current.CancellationToken;
        var submission = await SubmitAndWaitForGroupCompletionAsync();

        var response = await Client.GetAsync(
            $"/api/optimizations/groups/{submission.GroupId}/trials?sortBy=SharpeRatio", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var paged = await response.Content.ReadFromJsonAsync<PagedResponse<BacktestRunResponse>>(Json, ct);
        Assert.NotNull(paged);
        Assert.NotEmpty(paged.Items);
    }

    [Fact]
    public async Task GetGroupTrials_WithPagination_RespectsLimitOffset()
    {
        var ct = TestContext.Current.CancellationToken;
        var submission = await SubmitAndWaitForGroupCompletionAsync();

        var response = await Client.GetAsync(
            $"/api/optimizations/groups/{submission.GroupId}/trials?limit=1&offset=0", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var paged = await response.Content.ReadFromJsonAsync<PagedResponse<BacktestRunResponse>>(Json, ct);
        Assert.NotNull(paged);
        Assert.Single(paged.Items);
        Assert.True(paged.TotalCount >= 1);
    }

    [Fact]
    public async Task GetGroupTrials_IncludesParamsField()
    {
        var ct = TestContext.Current.CancellationToken;
        var submission = await SubmitAndWaitForGroupCompletionAsync();

        var response = await Client.GetAsync(
            $"/api/optimizations/groups/{submission.GroupId}/trials", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var paged = await response.Content.ReadFromJsonAsync<PagedResponse<BacktestRunResponse>>(Json, ct);
        Assert.NotNull(paged);
        Assert.NotEmpty(paged.Items);

        foreach (var trial in paged.Items)
        {
            Assert.NotNull(trial.Params);
            Assert.Contains("Quantity", trial.Params);
        }
    }
}
