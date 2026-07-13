using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Infrastructure.Index;
using AlgoTradeForge.HistoryLoader.WebApi.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Endpoints;

public sealed class JobEndpointsTests : IAsyncLifetime, IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("atf-jobs-ep-").FullName;
    private SqliteHistoryIndex _index = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var init = new HistoryIndexInitializer(Path.Combine(_dir, "idx.sqlite"));
        await init.EnsureCreated(Ct);
        _index = new SqliteHistoryIndex(init, init.ConnectionString + ";Pooling=False");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task GetJob_ReturnsUnifiedEnvelope()
    {
        var jobId = (await _index.TryAcquireFeedGate("load", "binance|BTCUSDT|candles|1m",
            """{"phase":"2024-03","done":3,"total":12,"detail":{"current_month":"2024-03"}}""", "{}", Ct)
            as FeedGateOutcome.Acquired)!.JobId;

        var result = await JobEndpoints.GetJob(jobId, _index, Ct);
        var env = Assert.IsType<Ok<JobEnvelope>>(result).Value!;
        Assert.Equal("load", env.Kind);
        Assert.Equal("queued", env.State);
        Assert.Equal("binance|BTCUSDT|candles|1m", env.FeedKey);
        Assert.Equal(3, env.Progress!.Done);
    }

    [Fact]
    public async Task ListJobs_FiltersByKindAndState_ReturnsMatchingSubset()
    {
        await _index.TryAcquireFeedGate("load", "binance|BTCUSDT|candles|1m",
            """{"phase":"2024-01","done":1,"total":5}""", "{}", Ct);
        await _index.CreateJob("rebuild", Ct);

        // Filter by kind=load → only 1 result
        var result = await JobEndpoints.ListJobs("load", null, _index, Ct);
        var loadJobs = Assert.IsType<Ok<List<JobEnvelope>>>(result).Value!;
        Assert.Single(loadJobs);
        Assert.Equal("load", loadJobs[0].Kind);
        Assert.Equal("binance|BTCUSDT|candles|1m", loadJobs[0].FeedKey);

        // Filter by kind=rebuild → only 1 result, feed_key null (index job)
        var result2 = await JobEndpoints.ListJobs("rebuild", null, _index, Ct);
        var rebuildJobs = Assert.IsType<Ok<List<JobEnvelope>>>(result2).Value!;
        Assert.Single(rebuildJobs);
        Assert.Equal("rebuild", rebuildJobs[0].Kind);
        Assert.Null(rebuildJobs[0].FeedKey);

        // No filter → both results
        var result3 = await JobEndpoints.ListJobs(null, null, _index, Ct);
        var all = Assert.IsType<Ok<List<JobEnvelope>>>(result3).Value!;
        Assert.Equal(2, all.Count);

        // Filter by state=queued → both results (both are queued)
        var result4 = await JobEndpoints.ListJobs(null, "queued", _index, Ct);
        var queued = Assert.IsType<Ok<List<JobEnvelope>>>(result4).Value!;
        Assert.Equal(2, queued.Count);
    }

    [Fact]
    public async Task GetJob_UnknownId_Returns404()
    {
        var result = await JobEndpoints.GetJob("doesnotexist", _index, Ct);
        var statusResult = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, statusResult.StatusCode);
    }
}
