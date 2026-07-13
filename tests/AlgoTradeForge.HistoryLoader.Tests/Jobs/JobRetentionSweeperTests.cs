using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Infrastructure.Index;
using AlgoTradeForge.HistoryLoader.WebApi.Jobs;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Jobs;

public sealed class JobRetentionSweeperTests : IAsyncLifetime, IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("atf-sweeper-").FullName;
    private SqliteHistoryIndex _index = null!;
    private string _connectionString = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var init = new HistoryIndexInitializer(Path.Combine(_dir, "idx.sqlite"));
        await init.EnsureCreated(Ct);
        _connectionString = init.ConnectionString + ";Pooling=False";
        _index = new SqliteHistoryIndex(init, _connectionString);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    // Directly rewrites updated_at so DeleteTerminalJobsBefore sees an old timestamp.
    private async Task BackdateUpdatedAt(string jobId, DateTimeOffset newTime)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = conn.CreateCommand();
        cmd.Parameters.AddWithValue("$o", newTime.UtcDateTime.ToString("O"));
        cmd.Parameters.AddWithValue("$id", jobId);
        cmd.CommandText = "UPDATE index_jobs SET updated_at=$o WHERE id=$id";
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    [Fact]
    public async Task Sweep_DeletesExpiredTerminalJobs_KeepsFresh()
    {
        var old = await _index.CreateJob("load", Ct);
        await _index.UpdateJob(old, "complete", ct: Ct);
        await BackdateUpdatedAt(old, DateTimeOffset.UtcNow.AddHours(-2));

        var fresh = await _index.CreateJob("load", Ct);
        await _index.UpdateJob(fresh, "complete", ct: Ct);

        await JobRetentionSweeper.SweepOnceForTest(_index, TimeSpan.FromMinutes(30), Ct);

        Assert.Null(await _index.GetJob(old, Ct));
        Assert.NotNull(await _index.GetJob(fresh, Ct));
    }

    [Fact]
    public async Task Sweep_KeepsRunningAndQueuedJobs_RegardlessOfAge()
    {
        var running = await _index.CreateJob("load", Ct);
        await _index.UpdateJob(running, "running", ct: Ct);
        await BackdateUpdatedAt(running, DateTimeOffset.UtcNow.AddHours(-2));

        var queued = await _index.CreateJob("load", Ct);
        // queued is the initial state — just backdate it
        await BackdateUpdatedAt(queued, DateTimeOffset.UtcNow.AddHours(-2));

        await JobRetentionSweeper.SweepOnceForTest(_index, TimeSpan.FromMinutes(30), Ct);

        Assert.NotNull(await _index.GetJob(running, Ct));
        Assert.NotNull(await _index.GetJob(queued, Ct));
    }
}
