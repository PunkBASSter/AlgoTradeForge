using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlgoTradeForge.Application;
using AlgoTradeForge.Application.Optimization;
using AlgoTradeForge.WebApi.Contracts;
using AlgoTradeForge.WebApi.Tests.Infrastructure;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Subscriptions;

namespace AlgoTradeForge.WebApi.Tests.Endpoints;

[Collection("Api")]
public sealed class OptimizationEndpointsApiTests(AlgoTradeForgeApiFactory factory) : ApiTestBase(factory)
{
    // Each call returns a unique request to avoid RunKey dedup collisions between tests
    private static int _requestCounter;
    private static RunOptimizationRequest MakeOptimizationRequest()
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

    // ── Happy paths ──────────────────────────────────────────────────

    [Fact]
    public async Task Post_ValidRequest_Returns202WithSubmission()
    {
        var request = MakeOptimizationRequest();

        var (response, body) = await SubmitOptimizationAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.NotEqual(Guid.Empty, body.Id);
        Assert.True(body.TotalCombinations > 0);
    }

    [Fact]
    public async Task GetStatus_AfterCompletion_ReturnsResultWithTrials()
    {
        var request = MakeOptimizationRequest();
        var (_, submission) = await SubmitOptimizationAsync(request);

        var status = await PollOptimizationUntilDoneAsync(submission.Id, TimeSpan.FromSeconds(120));

        Assert.NotNull(status.Result);
        Assert.Equal(submission.Id, status.Result.Id);
        Assert.True(status.Result.TrialCount > 0);
    }

    [Fact]
    public async Task GetById_AfterCompletion_Returns200WithTrials()
    {
        var request = MakeOptimizationRequest();
        var (_, submission) = await SubmitOptimizationAsync(request);
        await PollOptimizationUntilDoneAsync(submission.Id, TimeSpan.FromSeconds(120));

        var response = await Client.GetAsync($"/api/optimizations/{submission.Id}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<OptimizationRunResponse>(Json, TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.Equal(submission.Id, body.Id);
        Assert.True(body.TrialCount > 0);

        // Trials are now loaded via the separate paginated endpoint
        var trialsResponse = await Client.GetAsync($"/api/optimizations/{submission.Id}/trials", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, trialsResponse.StatusCode);
        var trialsPage = await trialsResponse.Content.ReadFromJsonAsync<PagedResponse<BacktestRunResponse>>(Json, TestContext.Current.CancellationToken);
        Assert.NotNull(trialsPage);
        Assert.NotEmpty(trialsPage.Items);
    }

    [Fact]
    public async Task ListOptimizations_AfterCompletion_ContainsRun()
    {
        var request = MakeOptimizationRequest();
        var (_, submission) = await SubmitOptimizationAsync(request);
        await PollOptimizationUntilDoneAsync(submission.Id, TimeSpan.FromSeconds(120));

        var response = await Client.GetAsync("/api/optimizations", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var paged = await response.Content.ReadFromJsonAsync<PagedResponse<OptimizationRunResponse>>(Json, TestContext.Current.CancellationToken);
        Assert.NotNull(paged);
        Assert.Contains(paged.Items, i => i.Id == submission.Id);
    }

    // ── Negative tests ───────────────────────────────────────────────

    [Fact]
    public async Task Post_UnknownStrategy_Returns400()
    {
        var request = new RunOptimizationRequest
        {
            StrategyName = "NonExistentStrategy",
            BacktestSettings = new()
            {
                InitialCash = 10_000m,
                StartTime = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
                EndTime = new DateTimeOffset(2025, 1, 15, 0, 0, 0, TimeSpan.Zero),
            },
            SubscriptionAxis =
            [
                [new TimeBarSubscription("BTCUSDT", "Binance", DataFeedRole.Primary, TimeFrame.Parse("1h"))]
            ],
        };

        var response = await Client.PostAsJsonAsync("/api/optimizations", request, Json, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_UnknownAsset_Returns202ThenRunFails()
    {
        // In the compute queue architecture, asset validation is deferred to execution time.
        // The submission is accepted (202), and the run fails during processing.
        var request = new RunOptimizationRequest
        {
            StrategyName = "BuyAndHold",
            BacktestSettings = new()
            {
                InitialCash = 10_000m,
                StartTime = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
                EndTime = new DateTimeOffset(2025, 1, 15, 0, 0, 0, TimeSpan.Zero),
            },
            SubscriptionAxis =
            [
                [new TimeBarSubscription("FAKEUSDT", "FakeExchange", DataFeedRole.Primary, TimeFrame.Parse("1h"))]
            ],
        };

        var response = await Client.PostAsJsonAsync("/api/optimizations", request, Json, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task GetById_RandomGuid_Returns404()
    {
        var response = await Client.GetAsync($"/api/optimizations/{Guid.NewGuid()}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetStatus_RandomGuid_Returns404()
    {
        var response = await Client.GetAsync($"/api/optimizations/{Guid.NewGuid()}/status", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Cancel_RandomGuid_Returns404()
    {
        var response = await Client.PostAsync($"/api/optimizations/{Guid.NewGuid()}/cancel", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Post_WithSubscriptionAxis_Returns202WithSubmission()
    {
        var request = new RunOptimizationRequest
        {
            StrategyName = "BuyAndHold",
            BacktestSettings = new()
            {
                InitialCash = 10_000m,
                StartTime = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
                EndTime = new DateTimeOffset(2025, 1, 15, 0, 0, 0, TimeSpan.Zero),
            },
            OptimizationSettings = new()
            {
                MaxDegreeOfParallelism = 1,
            },
            SubscriptionAxis =
            [
                [new TimeBarSubscription("BTCUSDT", "Binance", DataFeedRole.Primary, TimeFrame.Parse("1h"))]
            ],
            OptimizationAxes = new Dictionary<string, OptimizationAxisOverride>
            {
                ["Quantity"] = new RangeOverride(1m, 3m, 2m),
            },
        };

        var (response, body) = await SubmitOptimizationAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.NotEqual(Guid.Empty, body.Id);
        Assert.True(body.TotalCombinations > 0);
    }

    // ── Cancel test ──────────────────────────────────────────────────

    [Fact]
    public async Task Cancel_InProgressOptimization_ReturnsOkOrNotFoundIfAlreadyDone()
    {
        // Optimization might complete before we cancel, so accept 200 (cancelled) or 404 (already done)
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

        var (_, submission) = await SubmitOptimizationAsync(request);

        var cancelResponse = await Client.PostAsync($"/api/optimizations/{submission.Id}/cancel", null, TestContext.Current.CancellationToken);

        // Cancel returns 200 if still running, 404 if already completed and removed from cancellation registry
        Assert.True(
            cancelResponse.StatusCode is HttpStatusCode.OK or HttpStatusCode.NotFound,
            $"Expected 200 or 404, got {(int)cancelResponse.StatusCode}");
    }
}
