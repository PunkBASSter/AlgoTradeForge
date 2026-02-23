using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlgoTradeForge.WebApi.Contracts;
using AlgoTradeForge.WebApi.Tests.Infrastructure;

namespace AlgoTradeForge.WebApi.Tests.Endpoints;

[Collection("Api")]
public sealed class BacktestEndpointsApiTests(AlgoTradeForgeApiFactory factory) : ApiTestBase(factory)
{
    // ── Happy paths ──────────────────────────────────────────────────

    [Fact]
    public async Task Post_ValidRequest_Returns202WithSubmission()
    {
        var request = MakeBacktestRequest();

        var (response, body) = await SubmitBacktestAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.NotEqual(Guid.Empty, body.Id);
        Assert.True(body.TotalBars > 0);
    }

    [Fact]
    public async Task GetStatus_AfterCompletion_ReturnsResultWithMetrics()
    {
        var request = MakeBacktestRequest();
        var (_, submission) = await SubmitBacktestAsync(request);

        var status = await PollBacktestUntilDoneAsync(submission.Id, TimeSpan.FromSeconds(60));

        Assert.NotNull(status.Result);
        Assert.Equal(submission.Id, status.Result.Id);
        Assert.Equal("ZigZagBreakout", status.Result.StrategyName);
        Assert.Equal("BTCUSDT", status.Result.AssetName);
        Assert.Equal("Binance", status.Result.Exchange);
        Assert.True(status.Result.Metrics.ContainsKey("totalTrades"));
        Assert.True(status.Result.Metrics.ContainsKey("sharpeRatio"));
        Assert.True(status.Result.Metrics.ContainsKey("netProfit"));
    }

    [Fact]
    public async Task GetById_AfterCompletion_Returns200WithFullResponse()
    {
        var request = MakeBacktestRequest();
        var (_, submission) = await SubmitBacktestAsync(request);
        await PollBacktestUntilDoneAsync(submission.Id, TimeSpan.FromSeconds(60));

        var response = await Client.GetAsync($"/api/backtests/{submission.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<BacktestRunResponse>(Json);
        Assert.NotNull(body);
        Assert.Equal(submission.Id, body.Id);
        Assert.Equal("Backtest", body.RunMode);
    }

    [Fact]
    public async Task GetEquity_AfterCompletion_ReturnsNonEmptyCurve()
    {
        // Delay to avoid event log folder timestamp collision with concurrent background runs
        await Task.Delay(1100);

        var request = MakeBacktestRequest(
            startTime: new DateTimeOffset(2025, 1, 5, 0, 0, 0, TimeSpan.Zero),
            endTime: new DateTimeOffset(2025, 1, 12, 0, 0, 0, TimeSpan.Zero));
        var (_, submission) = await SubmitBacktestAsync(request);
        var status = await PollBacktestUntilDoneAsync(submission.Id, TimeSpan.FromSeconds(60));

        Assert.True(status.TotalBars > 0, $"Expected TotalBars > 0 but backtest may have failed");

        var response = await Client.GetAsync($"/api/backtests/{submission.Id}/equity");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(raw);
        Assert.True(doc.RootElement.GetArrayLength() > 0, "Expected non-empty equity curve");
    }

    [Fact]
    public async Task ListBacktests_AfterCompletion_ContainsRun()
    {
        var request = MakeBacktestRequest();
        var (_, submission) = await SubmitBacktestAsync(request);
        await PollBacktestUntilDoneAsync(submission.Id, TimeSpan.FromSeconds(60));

        var response = await Client.GetAsync("/api/backtests");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var paged = await response.Content.ReadFromJsonAsync<PagedResponse<BacktestRunResponse>>(Json);
        Assert.NotNull(paged);
        Assert.True(paged.Items.Count > 0);
        Assert.Contains(paged.Items, i => i.Id == submission.Id);
    }

    [Theory]
    [InlineData("strategyName=ZigZagBreakout", true)]
    [InlineData("assetName=BTCUSDT", true)]
    [InlineData("exchange=Binance", true)]
    [InlineData("strategyName=NonExistent", false)]
    public async Task ListBacktests_WithFilters_ReturnsFilteredResults(string queryString, bool expectResults)
    {
        var request = MakeBacktestRequest();
        var (_, submission) = await SubmitBacktestAsync(request);
        await PollBacktestUntilDoneAsync(submission.Id, TimeSpan.FromSeconds(60));

        var response = await Client.GetAsync($"/api/backtests?{queryString}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var paged = await response.Content.ReadFromJsonAsync<PagedResponse<BacktestRunResponse>>(Json);
        Assert.NotNull(paged);
        if (expectResults)
            Assert.True(paged.Items.Count > 0);
        else
            Assert.Empty(paged.Items);
    }

    // ── Negative tests ───────────────────────────────────────────────

    [Theory]
    [InlineData("invalid")]
    [InlineData("abc:def")]
    public async Task Post_InvalidTimeFrame_Returns400(string badTimeFrame)
    {
        var request = MakeBacktestRequest(timeFrame: badTimeFrame);

        var response = await Client.PostAsJsonAsync("/api/backtests", request, Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_UnknownStrategy_Returns400()
    {
        var request = MakeBacktestRequest(strategyName: "NonExistentStrategy");

        var response = await Client.PostAsJsonAsync("/api/backtests", request, Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_UnknownAsset_Returns400()
    {
        var request = new RunBacktestRequest
        {
            AssetName = "FAKEUSDT",
            Exchange = "FakeExchange",
            StrategyName = "ZigZagBreakout",
            InitialCash = 10_000m,
            StartTime = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            EndTime = new DateTimeOffset(2025, 1, 15, 0, 0, 0, TimeSpan.Zero),
            TimeFrame = "01:00:00",
        };

        var response = await Client.PostAsJsonAsync("/api/backtests", request, Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetById_RandomGuid_Returns404()
    {
        var response = await Client.GetAsync($"/api/backtests/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetEquity_RandomGuid_Returns404()
    {
        var response = await Client.GetAsync($"/api/backtests/{Guid.NewGuid()}/equity");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetStatus_RandomGuid_Returns404()
    {
        var response = await Client.GetAsync($"/api/backtests/{Guid.NewGuid()}/status");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Cancel_RandomGuid_Returns404()
    {
        var response = await Client.PostAsync($"/api/backtests/{Guid.NewGuid()}/cancel", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Cancel test ──────────────────────────────────────────────────

    [Fact]
    public async Task Cancel_InProgressBacktest_ReturnsOkOrNotFoundIfAlreadyDone()
    {
        // Use a larger date range so the backtest takes longer
        var request = MakeBacktestRequest(
            startTime: new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            endTime: new DateTimeOffset(2025, 4, 1, 0, 0, 0, TimeSpan.Zero));

        var (_, submission) = await SubmitBacktestAsync(request);

        // Immediately cancel — may already be done on fast machines
        var cancelResponse = await Client.PostAsync($"/api/backtests/{submission.Id}/cancel", null);
        Assert.True(
            cancelResponse.StatusCode is HttpStatusCode.OK or HttpStatusCode.NotFound,
            $"Expected 200 or 404, got {(int)cancelResponse.StatusCode}");
    }

    // ── Dedup test ───────────────────────────────────────────────────

    [Fact]
    public async Task Post_IdenticalRequest_ReturnsSameId()
    {
        var request = MakeBacktestRequest(
            startTime: new DateTimeOffset(2025, 2, 1, 0, 0, 0, TimeSpan.Zero),
            endTime: new DateTimeOffset(2025, 2, 10, 0, 0, 0, TimeSpan.Zero));

        var (_, first) = await SubmitBacktestAsync(request);
        var (_, second) = await SubmitBacktestAsync(request);

        Assert.Equal(first.Id, second.Id);
    }
}
