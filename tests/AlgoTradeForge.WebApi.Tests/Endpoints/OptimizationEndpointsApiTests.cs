using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlgoTradeForge.Application.Optimization;
using AlgoTradeForge.WebApi.Contracts;
using AlgoTradeForge.WebApi.Tests.Infrastructure;

namespace AlgoTradeForge.WebApi.Tests.Endpoints;

[Collection("Api")]
public sealed class OptimizationEndpointsApiTests(AlgoTradeForgeApiFactory factory) : ApiTestBase(factory)
{
    private static RunOptimizationRequest MakeOptimizationRequest() => new()
    {
        StrategyName = "ZigZagBreakout",
        DataSubscriptions =
        [
            new DataSubscriptionDto
            {
                Asset = "BTCUSDT",
                Exchange = "Binance",
                TimeFrame = "01:00:00",
            }
        ],
        OptimizationAxes = new Dictionary<string, OptimizationAxisOverride>
        {
            ["DzzDepth"] = new RangeOverride(4m, 6m, 2m), // 2 values: 4, 6
        },
        InitialCash = 10_000m,
        StartTime = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
        EndTime = new DateTimeOffset(2025, 1, 15, 0, 0, 0, TimeSpan.Zero),
        MaxDegreeOfParallelism = 1,
    };

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
        Assert.NotEmpty(status.Result.Trials);
    }

    [Fact]
    public async Task GetById_AfterCompletion_Returns200WithTrials()
    {
        var request = MakeOptimizationRequest();
        var (_, submission) = await SubmitOptimizationAsync(request);
        await PollOptimizationUntilDoneAsync(submission.Id, TimeSpan.FromSeconds(120));

        var response = await Client.GetAsync($"/api/optimizations/{submission.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<OptimizationRunResponse>(Json);
        Assert.NotNull(body);
        Assert.Equal(submission.Id, body.Id);
        Assert.NotEmpty(body.Trials);
    }

    [Fact]
    public async Task ListOptimizations_AfterCompletion_ContainsRun()
    {
        var request = MakeOptimizationRequest();
        var (_, submission) = await SubmitOptimizationAsync(request);
        await PollOptimizationUntilDoneAsync(submission.Id, TimeSpan.FromSeconds(120));

        var response = await Client.GetAsync("/api/optimizations");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var paged = await response.Content.ReadFromJsonAsync<PagedResponse<OptimizationRunResponse>>(Json);
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
            DataSubscriptions =
            [
                new DataSubscriptionDto
                {
                    Asset = "BTCUSDT",
                    Exchange = "Binance",
                    TimeFrame = "01:00:00",
                }
            ],
            InitialCash = 10_000m,
            StartTime = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            EndTime = new DateTimeOffset(2025, 1, 15, 0, 0, 0, TimeSpan.Zero),
        };

        var response = await Client.PostAsJsonAsync("/api/optimizations", request, Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_UnknownAsset_Returns400()
    {
        var request = new RunOptimizationRequest
        {
            StrategyName = "ZigZagBreakout",
            DataSubscriptions =
            [
                new DataSubscriptionDto
                {
                    Asset = "FAKEUSDT",
                    Exchange = "FakeExchange",
                    TimeFrame = "01:00:00",
                }
            ],
            InitialCash = 10_000m,
            StartTime = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            EndTime = new DateTimeOffset(2025, 1, 15, 0, 0, 0, TimeSpan.Zero),
        };

        var response = await Client.PostAsJsonAsync("/api/optimizations", request, Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetById_RandomGuid_Returns404()
    {
        var response = await Client.GetAsync($"/api/optimizations/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetStatus_RandomGuid_Returns404()
    {
        var response = await Client.GetAsync($"/api/optimizations/{Guid.NewGuid()}/status");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Cancel_RandomGuid_Returns404()
    {
        var response = await Client.PostAsync($"/api/optimizations/{Guid.NewGuid()}/cancel", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Cancel test ──────────────────────────────────────────────────

    [Fact]
    public async Task Cancel_InProgressOptimization_ReturnsOkOrNotFoundIfAlreadyDone()
    {
        // Optimization might complete before we cancel, so accept 200 (cancelled) or 404 (already done)
        var request = new RunOptimizationRequest
        {
            StrategyName = "ZigZagBreakout",
            DataSubscriptions =
            [
                new DataSubscriptionDto
                {
                    Asset = "BTCUSDT",
                    Exchange = "Binance",
                    TimeFrame = "01:00:00",
                }
            ],
            OptimizationAxes = new Dictionary<string, OptimizationAxisOverride>
            {
                ["DzzDepth"] = new RangeOverride(1m, 20m, 0.5m),
                ["RiskPercentPerTrade"] = new RangeOverride(0.5m, 3m, 0.5m),
            },
            InitialCash = 10_000m,
            StartTime = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            EndTime = new DateTimeOffset(2025, 4, 1, 0, 0, 0, TimeSpan.Zero),
            MaxDegreeOfParallelism = 1,
        };

        var (_, submission) = await SubmitOptimizationAsync(request);

        var cancelResponse = await Client.PostAsync($"/api/optimizations/{submission.Id}/cancel", null);

        // Cancel returns 200 if still running, 404 if already completed and removed from cancellation registry
        Assert.True(
            cancelResponse.StatusCode is HttpStatusCode.OK or HttpStatusCode.NotFound,
            $"Expected 200 or 404, got {(int)cancelResponse.StatusCode}");
    }
}
