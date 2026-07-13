using System.Text.Json;
using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Application.Jobs;
using AlgoTradeForge.HistoryLoader.Infrastructure.Index;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Jobs;

public sealed class JobProgressSinkTests : IAsyncLifetime, IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("atf-sink-").FullName;
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
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task Sink_Report_AppendsEventUpdatesRowSignals()
    {
        var jobId = await _index.CreateJob("load", Ct);
        var signal = new JobEventSignal();
        var sink = new JobProgressSink(jobId, _index, signal);

        var woke = signal.Next(jobId);
        await sink.Report("""{"phase":"2024-03","done":3,"total":12}""", Ct);
        Assert.True(woke.IsCompleted);
        Assert.Equal(1, await _index.GetLastEventSeq(jobId, Ct));
        Assert.Contains("2024-03", (await _index.GetJob(jobId, Ct))!.ProgressJson);

        await sink.Complete("""{"ok":true}""", Ct);
        Assert.Equal("complete", (await _index.GetJob(jobId, Ct))!.State);
    }

    [Fact]
    public async Task Sink_Started_SetsStateRunning()
    {
        var jobId = await _index.CreateJob("load", Ct);
        var signal = new JobEventSignal();
        var sink = new JobProgressSink(jobId, _index, signal);

        var woke = signal.Next(jobId);
        await sink.Started("""{"info":"kicked"}""", Ct);

        Assert.True(woke.IsCompleted);
        var row = await _index.GetJob(jobId, Ct);
        Assert.Equal("running", row!.State);
        Assert.Equal(1, await _index.GetLastEventSeq(jobId, Ct));
        var events = await _index.GetJobEventsAfter(jobId, 0, Ct);
        Assert.Equal("started", events[0].Kind);
    }

    [Fact]
    public async Task Sink_Complete_SetsStateAndEvictsCell()
    {
        var jobId = await _index.CreateJob("load", Ct);
        var signal = new JobEventSignal();
        var sink = new JobProgressSink(jobId, _index, signal);

        // Register a cell before completing — it must be signalled (not just evicted silently).
        var woke = signal.Next(jobId);
        await sink.Complete("""{"ok":true}""", Ct);

        Assert.True(woke.IsCompleted);
        var row = await _index.GetJob(jobId, Ct);
        Assert.Equal("complete", row!.State);

        // After Evict, Next creates a fresh unsignalled cell — proves cell was evicted.
        var afterEvict = signal.Next(jobId);
        Assert.False(afterEvict.IsCompleted);
    }

    [Fact]
    public async Task Sink_Fail_SetsStateError()
    {
        var jobId = await _index.CreateJob("load", Ct);
        var signal = new JobEventSignal();
        var sink = new JobProgressSink(jobId, _index, signal);

        var woke = signal.Next(jobId);
        await sink.Fail("http_error", "upstream 500", Ct);

        Assert.True(woke.IsCompleted);
        var row = await _index.GetJob(jobId, Ct);
        Assert.Equal("error", row!.State);
        Assert.NotNull(row.Error);
        Assert.Contains("http_error", row.Error);
        Assert.Contains("upstream 500", row.Error);
    }

    [Fact]
    public async Task Sink_Cancel_SetsStateCancelled()
    {
        var jobId = await _index.CreateJob("load", Ct);
        var signal = new JobEventSignal();
        var sink = new JobProgressSink(jobId, _index, signal);

        var woke = signal.Next(jobId);
        await sink.Cancel("user_requested", Ct);

        Assert.True(woke.IsCompleted);
        var row = await _index.GetJob(jobId, Ct);
        Assert.Equal("cancelled", row!.State);
    }

    [Fact]
    public async Task Sink_Fail_WithQuotesInMessage_StoresValidJson()
    {
        var jobId = await _index.CreateJob("load", Ct);
        var signal = new JobEventSignal();
        var sink = new JobProgressSink(jobId, _index, signal);

        await sink.Fail("load_failed", "bad \"symbol\" value", Ct);

        var row = await _index.GetJob(jobId, Ct);
        Assert.NotNull(row!.Error);
        var parsed = JsonSerializer.Deserialize<JsonElement>(row.Error);
        Assert.Equal("load_failed", parsed.GetProperty("code").GetString());
        Assert.Equal("bad \"symbol\" value", parsed.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Factory_For_BindsJobId()
    {
        var jobId = await _index.CreateJob("load", Ct);
        var signal = new JobEventSignal();
        var factory = new JobProgressSinkFactory(_index, signal);
        var sink = factory.For(jobId);

        await sink.Report("""{"phase":"2024-01","done":1,"total":1}""", Ct);
        Assert.Equal(1, await _index.GetLastEventSeq(jobId, Ct));
    }
}
