using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Aggregation;
using AlgoTradeForge.HistoryLoader.Application.Aggregation.Jobs;
using AlgoTradeForge.HistoryLoader.Tests.TestHelpers;
using Microsoft.Extensions.Options;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Aggregation.Jobs;

/// <summary>
/// P1b-15 / P1b-16 / P1b-17 / P1b-29 / P1b-30 — registry + queue lifecycle including the
/// three feed_id dedup paths.
/// </summary>
public sealed class AggregationJobRegistryTests
{
    private static readonly DateTimeOffset T0 =
        new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private static AggregationJob NewJob(string jobId, string feedId)
    {
        var scale = new ScaleContext(tickSize: 0.01m, quantityStepSize: 0.0001m);
        return new AggregationJob(
            JobId: jobId,
            Source: new DataFeedDescriptor("/", "binance", "BTCUSDT", "1m", DataFeedKind.TimeBar),
            AssetDir: "/binance/BTCUSDT",
            OutcomeFeedId: feedId,
            TypeCode: "EqV",
            ThresholdAbsolute: 1000m,
            ThresholdScaled: 1000,
            ThresholdUnit: "base_asset",
            ThresholdInputMode: "absolute",
            ThresholdConvenienceInput: null,
            SourceScale: scale,
            AccumulatorScale: scale,
            MaxPartitionSizeMB: 100,
            ToolVersion: "test");
    }

    private static (AggregationJobRegistry registry, AggregationJobQueue queue, TestClock clock)
        BuildSubjects(int queueCapacity = 4, int retentionMinutes = 15)
    {
        var clock = new TestClock(T0);
        var options = Options.Create(new HistoryLoaderOptions
        {
            Aggregator = new AggregatorOptions
            {
                MaxQueueDepth = queueCapacity,
                JobRetentionMinutes = retentionMinutes,
            }
        });
        return (new AggregationJobRegistry(options, clock),
                new AggregationJobQueue(options),
                clock);
    }

    // -------------------------------------------------------------------------
    // TryEnqueue
    // -------------------------------------------------------------------------

    [Fact]
    public void TryEnqueue_Empty_AcceptsAndRecordsQueuedEvent()
    {
        var (reg, queue, _) = BuildSubjects();
        var outcome = reg.TryEnqueue(NewJob("j1", "EqV_1m_1000"), queue);

        var accepted = Assert.IsType<EnqueueOutcome.Accepted>(outcome);
        Assert.Equal(AggregationJobState.Queued, accepted.Record.State);
        var events = accepted.Record.EventsAfter(0);
        Assert.Single(events);
        Assert.IsType<ProgressEvent.Queued>(events[0].Event);
    }

    [Fact]
    public void TryEnqueue_FeedAlreadyRunning_ReturnsFeedAlreadyLocked()
    {
        var (reg, queue, _) = BuildSubjects();
        reg.TryEnqueue(NewJob("j1", "EqV_1m_1000"), queue);
        reg.OnStarted("j1", "1m");

        var outcome = reg.TryEnqueue(NewJob("j2", "EqV_1m_1000"), queue);

        var conflict = Assert.IsType<EnqueueOutcome.FeedAlreadyLocked>(outcome);
        Assert.Equal("j1", conflict.ExistingJobId);
        Assert.Equal(AggregationJobState.Running, conflict.ExistingState);
    }

    [Fact]
    public void TryEnqueue_FeedTerminalWithinRetention_EvictsAndAccepts()
    {
        // Path (b) of P1b-30: terminal entry exists for this feed_id within retention →
        // fresh enqueue evicts the old record.
        var (reg, queue, clock) = BuildSubjects();
        reg.TryEnqueue(NewJob("j1", "EqV_1m_1000"), queue);
        reg.OnStarted("j1", "1m");
        reg.OnCompleted("j1", FakeResult("EqV_1m_1000"));

        clock.Advance(TimeSpan.FromMinutes(5));   // still within 15-min retention

        var outcome = reg.TryEnqueue(NewJob("j2", "EqV_1m_1000"), queue);
        Assert.IsType<EnqueueOutcome.Accepted>(outcome);

        // The first job has been evicted (lookup returns null).
        Assert.Null(reg.Get("j1"));
    }

    [Fact]
    public void TryEnqueue_FeedTerminalPastRetention_AcceptsWithCleanRegistry()
    {
        // Path (c): terminal record past retention → eviction is lazy on Get, but the active
        // index has already been cleared at completion, so enqueue proceeds normally.
        var (reg, queue, clock) = BuildSubjects();
        reg.TryEnqueue(NewJob("j1", "EqV_1m_1000"), queue);
        reg.OnStarted("j1", "1m");
        reg.OnCompleted("j1", FakeResult("EqV_1m_1000"));

        clock.Advance(TimeSpan.FromMinutes(16));   // past retention

        var outcome = reg.TryEnqueue(NewJob("j2", "EqV_1m_1000"), queue);
        Assert.IsType<EnqueueOutcome.Accepted>(outcome);
        Assert.Null(reg.Get("j1"));
    }

    [Fact]
    public void TryEnqueue_QueueFull_ReturnsQueueFullAndDoesNotMutateRegistry()
    {
        var (reg, queue, _) = BuildSubjects(queueCapacity: 2);
        reg.TryEnqueue(NewJob("j1", "EqV_1m_1000"), queue);
        reg.TryEnqueue(NewJob("j2", "EqT_1m_500"), queue);

        var outcome = reg.TryEnqueue(NewJob("j3", "EqD_1m_2k"), queue);

        Assert.IsType<EnqueueOutcome.QueueFull>(outcome);
        Assert.Null(reg.Get("j3"));   // record never added
    }

    // -------------------------------------------------------------------------
    // State transitions populate the event log for SSE replay
    // -------------------------------------------------------------------------

    [Fact]
    public void Lifecycle_ProgressAndComplete_AppendEvents()
    {
        var (reg, queue, _) = BuildSubjects();
        var accepted = (EnqueueOutcome.Accepted)reg.TryEnqueue(NewJob("j1", "EqV_1m_1000"), queue);

        reg.OnStarted("j1", "1m");
        reg.OnProgress("j1", currentPartition: "2024-03", barsEmitted: 1500, elapsedMs: 1000);
        reg.OnProgress("j1", currentPartition: "2024-04", barsEmitted: 3000, elapsedMs: 2000);
        reg.OnCompleted("j1", FakeResult("EqV_1m_1000"));

        var events = accepted.Record.EventsAfter(0);
        Assert.Equal(5, events.Count);
        Assert.IsType<ProgressEvent.Queued>(events[0].Event);
        Assert.IsType<ProgressEvent.Started>(events[1].Event);
        Assert.IsType<ProgressEvent.Progress>(events[2].Event);
        Assert.IsType<ProgressEvent.Progress>(events[3].Event);
        Assert.IsType<ProgressEvent.Complete>(events[4].Event);

        // Sequence numbers are monotonic from 1.
        for (var i = 0; i < events.Count; i++)
            Assert.Equal(i + 1, events[i].Sequence);
    }

    [Fact]
    public void OnCompleted_DropsActiveFeedIdIndex_AllowsImmediateReQueueWhenTerminalEvicted()
    {
        var (reg, queue, _) = BuildSubjects();
        reg.TryEnqueue(NewJob("j1", "EqV_1m_1000"), queue);
        reg.OnStarted("j1", "1m");
        reg.OnCompleted("j1", FakeResult("EqV_1m_1000"));

        // Active index released → second enqueue is accepted (eviction path b).
        var outcome = reg.TryEnqueue(NewJob("j2", "EqV_1m_1000"), queue);
        Assert.IsType<EnqueueOutcome.Accepted>(outcome);
    }

    [Fact]
    public async Task Snapshot_ConcurrentTerminalTransition_NeverObservesStateWithoutPayload()
    {
        // Reviewer Issue 4 — Snapshot must never return (State=Complete && Result=null) or
        // (State=Error && Error=null). MarkTerminal sets payload BEFORE State under the events
        // lock; Snapshot takes the same lock. Stress-tests the invariant under contention.
        var (reg, queue, _) = BuildSubjects(queueCapacity: 64);

        const int iterations = 64;
        var failures = 0;

        for (var i = 0; i < iterations; i++)
        {
            var feedId = $"EqV_1m_{1000 + i}";
            var jobId = $"j{i}";
            reg.TryEnqueue(NewJob(jobId, feedId), queue);
            reg.OnStarted(jobId, "1m");

            var record = reg.Get(jobId)!;
            var done = false;

            var snapshotter = Task.Factory.StartNew(() =>
            {
                while (!Volatile.Read(ref done))
                {
                    var snap = record.Snapshot();
                    if (snap.State == AggregationJobState.Complete && snap.Result is null)
                        Interlocked.Increment(ref failures);
                    if (snap.State == AggregationJobState.Error && snap.Error is null)
                        Interlocked.Increment(ref failures);
                }
            }, TaskCreationOptions.LongRunning);

            // Spin a tiny window so the snapshotter is mid-loop when we transition.
            Thread.SpinWait(1000);

            if (i % 2 == 0)
                reg.OnCompleted(jobId, FakeResult(feedId));
            else
                reg.OnErrored(jobId, "test_err", "boom", retryable: false);

            // Let the snapshotter observe the post-transition state at least once.
            Thread.SpinWait(1000);
            Volatile.Write(ref done, true);
            await snapshotter;
        }

        Assert.Equal(0, failures);
    }

    // -------------------------------------------------------------------------
    // CheckActiveFeedId — used by the DELETE endpoint's race guard (P1b review fix #3 / Edit 2).
    // -------------------------------------------------------------------------

    [Fact]
    public void CheckActiveFeedId_ReturnsActiveJobInfo_ForQueuedJob()
    {
        var (reg, queue, _) = BuildSubjects();
        reg.TryEnqueue(NewJob("j1", "EqV_1m_1000"), queue);

        var info = reg.CheckActiveFeedId("EqV_1m_1000");

        Assert.NotNull(info);
        Assert.Equal("j1", info!.JobId);
        Assert.Equal(AggregationJobState.Queued, info.State);
    }

    [Fact]
    public void CheckActiveFeedId_ReturnsActiveJobInfo_ForRunningJob()
    {
        var (reg, queue, _) = BuildSubjects();
        reg.TryEnqueue(NewJob("j1", "EqV_1m_1000"), queue);
        reg.OnStarted("j1", "1m");

        var info = reg.CheckActiveFeedId("EqV_1m_1000");

        Assert.NotNull(info);
        Assert.Equal(AggregationJobState.Running, info!.State);
    }

    [Fact]
    public void CheckActiveFeedId_ReturnsNull_ForTerminalJob()
    {
        var (reg, queue, _) = BuildSubjects();
        reg.TryEnqueue(NewJob("j1", "EqV_1m_1000"), queue);
        reg.OnStarted("j1", "1m");
        reg.OnCompleted("j1", FakeResult("EqV_1m_1000"));

        Assert.Null(reg.CheckActiveFeedId("EqV_1m_1000"));
    }

    [Fact]
    public void CheckActiveFeedId_ReturnsNull_ForUnknownFeed()
    {
        var (reg, _, _) = BuildSubjects();
        Assert.Null(reg.CheckActiveFeedId("EqV_1m_999"));
    }

    // -------------------------------------------------------------------------
    // OnErrored — snapshot consumers see the caller-supplied message verbatim. The redaction
    // policy lives at the call site (AggregationWorkerHost) per P1b review fix #2 / Edit 1;
    // the registry's job is to round-trip whatever it receives. Locking that contract here
    // means a future regression in the redaction template surfaces as a test failure on the
    // worker side, not silently as leaked detail in production SSE frames.
    // -------------------------------------------------------------------------

    [Fact]
    public void OnErrored_PreservesMessageVerbatim_ForSnapshotConsumers()
    {
        var (reg, queue, _) = BuildSubjects();
        reg.TryEnqueue(NewJob("j1", "EqV_1m_1000"), queue);
        reg.OnStarted("j1", "1m");

        const string redacted =
            "Aggregation job failed (IOException); see server logs (job_id=j1).";

        reg.OnErrored("j1", "internal_error", redacted, retryable: false);

        var snap = reg.Get("j1")!.Snapshot();
        Assert.Equal(AggregationJobState.Error, snap.State);
        Assert.NotNull(snap.Error);
        Assert.Equal(redacted, snap.Error!.Message);
        Assert.Equal("internal_error", snap.Error.Code);
        Assert.False(snap.Error.Retryable);
    }

    // -------------------------------------------------------------------------
    // P6-10 — TryRequestCancel + OnCancelled (Phase 6)
    // -------------------------------------------------------------------------

    [Fact]
    public void TryRequestCancel_QueuedJob_FiresCts_LeavesStateUnchangedUntilWorkerObserves()
    {
        // Cancellation is cooperative — TryRequestCancel only fires the CTS. The state flips
        // when the worker calls OnCancelled in response to the OperationCanceledException.
        var (reg, queue, _) = BuildSubjects();
        reg.TryEnqueue(NewJob("j1", "EqV_1m_1000"), queue);

        var cts = reg.GetCancellationToken("j1");
        Assert.False(cts.IsCancellationRequested);

        Assert.IsType<CancelRequestOutcome.Requested>(reg.TryRequestCancel("j1"));
        Assert.True(cts.IsCancellationRequested);                  // CTS fired

        // State stays Queued until the worker explicitly calls OnCancelled (mirrors host_shutdown
        // sequencing — the registry never auto-flips state from a cancel request alone).
        Assert.Equal(AggregationJobState.Queued, reg.Get("j1")!.State);
    }

    [Fact]
    public void OnCancelled_AfterTryRequestCancel_TransitionsToCancelled_AndCleansActiveByFeedId()
    {
        var (reg, queue, _) = BuildSubjects();
        reg.TryEnqueue(NewJob("j1", "EqV_1m_1000"), queue);
        reg.OnStarted("j1", "1m");
        reg.TryRequestCancel("j1");
        reg.OnCancelled("j1", "user_cancelled");

        var snap = reg.Get("j1")!.Snapshot();
        Assert.Equal(AggregationJobState.Cancelled, snap.State);
        Assert.Equal("user_cancelled", snap.CancellationReason);
        Assert.NotNull(snap.CompletedAt);

        // Active-feed_id index cleared — a fresh enqueue of the same feed id can proceed.
        Assert.Null(reg.CheckActiveFeedId("EqV_1m_1000"));

        // Terminal event is a Cancelled record.
        var events = reg.Get("j1")!.EventsAfter(0);
        Assert.IsType<ProgressEvent.Cancelled>(events[^1].Event);
    }

    [Fact]
    public void TryRequestCancel_TerminalJob_ReturnsAlreadyTerminal_WithObservedState()
    {
        var (reg, queue, _) = BuildSubjects();
        reg.TryEnqueue(NewJob("j1", "EqV_1m_1000"), queue);
        reg.OnStarted("j1", "1m");
        reg.OnCompleted("j1", FakeResult("EqV_1m_1000"));

        var outcome = reg.TryRequestCancel("j1");
        var terminal = Assert.IsType<CancelRequestOutcome.AlreadyTerminal>(outcome);
        Assert.Equal(AggregationJobState.Complete, terminal.State);
    }

    [Fact]
    public void TryRequestCancel_UnknownJob_ReturnsUnknown()
    {
        // Reviewer Issue B1 — Unknown is distinct from AlreadyTerminal; the endpoint maps to
        // 404 vs 409 respectively. The bool + out-state shape couldn't distinguish.
        var (reg, _, _) = BuildSubjects();
        Assert.IsType<CancelRequestOutcome.Unknown>(reg.TryRequestCancel("nonexistent"));
    }

    [Fact]
    public void OnCancelled_AfterAlreadyTerminal_IsIdempotent_NoStateChange()
    {
        // Race scenario: host_shutdown OnErrored landed first; user cancel arrives later.
        var (reg, queue, _) = BuildSubjects();
        reg.TryEnqueue(NewJob("j1", "EqV_1m_1000"), queue);
        reg.OnStarted("j1", "1m");
        reg.OnErrored("j1", "host_shutdown", "shutdown", retryable: true);

        reg.OnCancelled("j1", "user_cancelled");   // no-op

        var snap = reg.Get("j1")!.Snapshot();
        Assert.Equal(AggregationJobState.Error, snap.State);   // still error, not cancelled
        Assert.Null(snap.CancellationReason);
    }

    private static AggregationResult FakeResult(string feedId) =>
        new(JobId: "fake", OutcomeFeedId: feedId,
            BarCount: 100, PartitionsWritten: ["2024-03.csv"],
            FirstBarTs: "1700000000000", LastBarTs: "1700000060000",
            ActualOvershootPct: 5d, MaxOvershootPct: 10d,
            EstimatedOvershootPct: 5d, MedianSourceRecordValue: 100d,
            NFactor: 10d, DurationSeconds: 1d);
}
