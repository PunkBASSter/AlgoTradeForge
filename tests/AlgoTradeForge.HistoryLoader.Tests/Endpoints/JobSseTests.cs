using AlgoTradeForge.HistoryLoader.Application.Jobs;
using AlgoTradeForge.HistoryLoader.Infrastructure.Index;
using AlgoTradeForge.HistoryLoader.WebApi.Endpoints;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Endpoints;

public sealed class JobSseTests : IAsyncLifetime, IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("atf-jobs-sse-").FullName;
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
    public async Task JobSse_TailLoop_DeliversEventAppendedBetweenCaptureAndDrain()
    {
        var jobId = await _index.CreateJob("load", Ct);
        var signal = new JobEventSignal();
        var sink = new JobProgressSink(jobId, _index, signal);
        var frames = new List<(int Seq, string Kind, string Payload)>();

        var loop = JobSseWriter.Tail(jobId, lastEventId: 0, _index, signal,
            emit: (seq, kind, payload) => { frames.Add((seq, kind, payload)); return Task.CompletedTask; },
            ct: Ct);

        await sink.Report("""{"done":1}""", Ct);
        await sink.Complete("{}", Ct);         // terminal — loop must return
        await loop.WaitAsync(TimeSpan.FromSeconds(2), Ct);

        Assert.Equal(new[] { "progress", "complete" }, frames.Select(f => f.Kind));
        Assert.Equal("""{"done":1}""", frames[0].Payload); // durable payload passed through verbatim
    }

    [Fact]
    public async Task JobSse_TailLoop_ResumesFromLastEventId()
    {
        var jobId = await _index.CreateJob("load", Ct);
        var signal = new JobEventSignal();
        var sink = new JobProgressSink(jobId, _index, signal);

        await sink.Report("""{"done":1}""", Ct);   // seq 1
        await sink.Report("""{"done":2}""", Ct);   // seq 2
        await sink.Complete("{}", Ct);             // seq 3 terminal

        var frames = new List<(int Seq, string Kind)>();
        var loop = JobSseWriter.Tail(jobId, lastEventId: 1, _index, signal,
            emit: (seq, kind, _) => { frames.Add((seq, kind)); return Task.CompletedTask; },
            ct: Ct);

        await loop.WaitAsync(TimeSpan.FromSeconds(2), Ct);

        // Only events with seq > 1 are delivered (seq 1 already seen), up to and incl. terminal.
        Assert.Equal(new[] { (2, "progress"), (3, "complete") }, frames);
    }

    [Fact]
    public async Task JobSse_TailLoop_DeliversEventsAppendedWhileRunning()
    {
        var jobId = await _index.CreateJob("load", Ct);
        var signal = new JobEventSignal();
        var sink = new JobProgressSink(jobId, _index, signal);
        var frames = new List<(int Seq, string Kind)>();

        // Loop starts BEFORE any events exist: its first `next` is captured over an empty tail.
        var loop = JobSseWriter.Tail(jobId, lastEventId: 0, _index, signal,
            emit: (seq, kind, _) => { frames.Add((seq, kind)); return Task.CompletedTask; },
            ct: Ct);

        // Each append fires Signal after the durable write; the running loop must observe every one,
        // regardless of whether it is mid-drain or parked on `next` when the append lands.
        for (var i = 1; i <= 5; i++)
            await sink.Report($$"""{"done":{{i}}}""", Ct);
        await sink.Complete("{}", Ct);

        await loop.WaitAsync(TimeSpan.FromSeconds(2), Ct);

        Assert.Equal(
            new[] { (1, "progress"), (2, "progress"), (3, "progress"), (4, "progress"), (5, "progress"), (6, "complete") },
            frames);
    }

    [Fact]
    public async Task JobSse_TailLoop_ResumePastTrimmedTail_ReplaysFromZero()
    {
        // Arrange: build a complete job with 3 durable events, all appended before the loop starts.
        var jobId = await _index.CreateJob("load", Ct);
        var signal = new JobEventSignal();
        var sink = new JobProgressSink(jobId, _index, signal);

        await sink.Report("""{"done":1}""", Ct);   // seq 1 — progress
        await sink.Report("""{"done":2}""", Ct);   // seq 2 — progress
        await sink.Complete("{}", Ct);             // seq 3 — complete (terminal)

        // Act: resume with lastEventId=99, which is above the actual max seq (3).
        // GetJobEventsAfter(jobId, 99) is empty, but GetLastEventSeq > 0, so the replay
        // branch must fire and return events from seq 0 onward.
        var frames = new List<(int Seq, string Kind)>();
        var loop = JobSseWriter.Tail(jobId, lastEventId: 99, _index, signal,
            emit: (seq, kind, _) => { frames.Add((seq, kind)); return Task.CompletedTask; },
            ct: Ct);

        // Without the replay branch the loop parks on next.WaitAsync (no future Signal will fire)
        // and this throws TimeoutException, proving non-vacuousness.
        await loop.WaitAsync(TimeSpan.FromSeconds(2), Ct);

        // Assert: full tail replayed from the beginning, ending with the terminal.
        Assert.Equal(new[] { (1, "progress"), (2, "progress"), (3, "complete") }, frames);
    }
}
