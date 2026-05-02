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
/// Integration tests for validation group endpoints (T066).
/// Creates an optimization group, waits for completion, then tests
/// validation group submission, detail, status, cancel, and delete.
/// </summary>
[Collection("Api")]
public sealed class ValidationEndpointGroupTests(AlgoTradeForgeApiFactory factory) : ApiTestBase(factory)
{
    // Each call returns a unique request to avoid RunKey dedup collisions between tests
    private static int _requestCounter;

    private async Task<OptimizationGroupSubmissionResponse> SubmitAndWaitForOptimizationGroupAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var offset = Interlocked.Increment(ref _requestCounter) * 100m;
        var request = new RunOptimizationRequest
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
                ["Quantity"] = new RangeOverride(1m, 3m, 2m),
            },
        };

        var response = await Client.PostAsJsonAsync("/api/optimizations", request, Json, ct);
        response.EnsureSuccessStatusCode();
        var submission = (await response.Content.ReadFromJsonAsync<OptimizationGroupSubmissionResponse>(Json, ct))!;

        foreach (var run in submission.Runs)
            await PollRunUntilTerminalAsync(run.Id, TimeSpan.FromSeconds(120));

        return submission;
    }

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

    // ── Submission ────────────────────────────────────────────────────

    [Fact]
    public async Task Post_GroupValidation_Returns202WithGroupId()
    {
        var ct = TestContext.Current.CancellationToken;
        var optGroup = await SubmitAndWaitForOptimizationGroupAsync();

        var request = new RunGroupValidationRequest
        {
            OptimizationGroupId = optGroup.GroupId,
            ThresholdProfileName = "Crypto-Standard",
            MaxTrialsToValidate = 10,
        };

        var response = await Client.PostAsJsonAsync("/api/validations/groups", request, Json, ct);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ValidationGroupSubmissionResponse>(Json, ct);
        Assert.NotNull(body);
        Assert.NotEqual(Guid.Empty, body.GroupId);
        Assert.Single(body.Runs);
        Assert.Equal(optGroup.Runs[0].Id, body.Runs[0].OptimizationRunId);
    }

    [Fact]
    public async Task Post_GroupValidation_ViaMainEndpoint_Returns202()
    {
        var ct = TestContext.Current.CancellationToken;
        var optGroup = await SubmitAndWaitForOptimizationGroupAsync();

        // POST /api/validations with optimizationGroupId dispatches to group handler
        var request = new RunValidationRequest
        {
            OptimizationGroupId = optGroup.GroupId,
            ThresholdProfileName = "Crypto-Standard",
            MaxTrialsToValidate = 10,
        };

        var response = await Client.PostAsJsonAsync("/api/validations", request, Json, ct);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ValidationGroupSubmissionResponse>(Json, ct);
        Assert.NotNull(body);
        Assert.NotEqual(Guid.Empty, body.GroupId);
    }

    [Fact]
    public async Task Post_GroupValidation_InvalidGroupId_Returns400()
    {
        var ct = TestContext.Current.CancellationToken;
        var request = new RunGroupValidationRequest
        {
            OptimizationGroupId = Guid.NewGuid(), // non-existent
            ThresholdProfileName = "Crypto-Standard",
        };

        var response = await Client.PostAsJsonAsync("/api/validations/groups", request, Json, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Detail ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetValidationGroup_ReturnsDetailWithChildRuns()
    {
        var ct = TestContext.Current.CancellationToken;
        var optGroup = await SubmitAndWaitForOptimizationGroupAsync();

        var submitRequest = new RunGroupValidationRequest
        {
            OptimizationGroupId = optGroup.GroupId,
            MaxTrialsToValidate = 10,
        };
        var submitResponse = await Client.PostAsJsonAsync("/api/validations/groups", submitRequest, Json, ct);
        var submission = (await submitResponse.Content.ReadFromJsonAsync<ValidationGroupSubmissionResponse>(Json, ct))!;

        // Wait briefly for processing to start
        await Task.Delay(500, ct);

        var response = await Client.GetAsync(
            $"/api/validations/groups/{submission.GroupId}", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var detail = await response.Content.ReadFromJsonAsync<ValidationGroupDetailResponse>(Json, ct);
        Assert.NotNull(detail);
        Assert.Equal(submission.GroupId, detail.Id);
        Assert.Equal(optGroup.GroupId, detail.OptimizationGroupId);
        Assert.Equal("BuyAndHold", detail.StrategyName);
        Assert.Single(detail.Runs);
    }

    // ── Status ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetValidationGroupStatus_ReturnsRunProgress()
    {
        var ct = TestContext.Current.CancellationToken;
        var optGroup = await SubmitAndWaitForOptimizationGroupAsync();

        var submitRequest = new RunGroupValidationRequest
        {
            OptimizationGroupId = optGroup.GroupId,
            MaxTrialsToValidate = 10,
        };
        var submitResponse = await Client.PostAsJsonAsync("/api/validations/groups", submitRequest, Json, ct);
        var submission = (await submitResponse.Content.ReadFromJsonAsync<ValidationGroupSubmissionResponse>(Json, ct))!;

        var response = await Client.GetAsync(
            $"/api/validations/groups/{submission.GroupId}/status", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var status = await response.Content.ReadFromJsonAsync<ValidationGroupStatusResponse>(Json, ct);
        Assert.NotNull(status);
        Assert.Equal(submission.GroupId, status.Id);
        Assert.Single(status.Runs);
    }

    // ── Cancel ────────────────────────────────────────────────────────

    [Fact]
    public async Task CancelValidationGroup_Returns204Or404()
    {
        var ct = TestContext.Current.CancellationToken;
        var optGroup = await SubmitAndWaitForOptimizationGroupAsync();

        var submitRequest = new RunGroupValidationRequest
        {
            OptimizationGroupId = optGroup.GroupId,
            MaxTrialsToValidate = 10,
        };
        var submitResponse = await Client.PostAsJsonAsync("/api/validations/groups", submitRequest, Json, ct);
        var submission = (await submitResponse.Content.ReadFromJsonAsync<ValidationGroupSubmissionResponse>(Json, ct))!;

        var response = await Client.PostAsync(
            $"/api/validations/groups/{submission.GroupId}/cancel", null, ct);

        Assert.True(
            response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound,
            $"Expected 204 or 404, got {(int)response.StatusCode}");
    }

    // ── Delete ────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteValidationGroup_CascadesAndReturns204()
    {
        var ct = TestContext.Current.CancellationToken;
        var optGroup = await SubmitAndWaitForOptimizationGroupAsync();

        var submitRequest = new RunGroupValidationRequest
        {
            OptimizationGroupId = optGroup.GroupId,
            MaxTrialsToValidate = 10,
        };
        var submitResponse = await Client.PostAsJsonAsync("/api/validations/groups", submitRequest, Json, ct);
        var submission = (await submitResponse.Content.ReadFromJsonAsync<ValidationGroupSubmissionResponse>(Json, ct))!;

        // Wait for validation to complete (or at least be persisted)
        await Task.Delay(2000, ct);

        var deleteResponse = await Client.DeleteAsync(
            $"/api/validations/groups/{submission.GroupId}", ct);
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        // Group should be gone
        var getResponse = await Client.GetAsync(
            $"/api/validations/groups/{submission.GroupId}", ct);
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    // ── 404 for non-existent groups ──────────────────────────────────

    [Fact]
    public async Task GetValidationGroup_RandomGuid_Returns404()
    {
        var response = await Client.GetAsync(
            $"/api/validations/groups/{Guid.NewGuid()}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetValidationGroupStatus_RandomGuid_Returns404()
    {
        var response = await Client.GetAsync(
            $"/api/validations/groups/{Guid.NewGuid()}/status", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CancelValidationGroup_RandomGuid_Returns404()
    {
        var response = await Client.PostAsync(
            $"/api/validations/groups/{Guid.NewGuid()}/cancel", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteValidationGroup_RandomGuid_Returns404()
    {
        var response = await Client.DeleteAsync(
            $"/api/validations/groups/{Guid.NewGuid()}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
