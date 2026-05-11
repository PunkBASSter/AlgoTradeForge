namespace AlgoTradeForge.HistoryLoader.Application.Aggregation.Jobs;

/// <summary>
/// Mutable in-registry view of one aggregation job. State mutation is gated by the registry's
/// per-feed_id lock; event-log mutation is gated by the record's own <c>_eventsLock</c> so
/// SSE handlers can snapshot events without contending with registry mutations for unrelated jobs.
/// </summary>
public sealed class AggregationJobRecord
{
    private readonly object _eventsLock = new();
    private readonly List<JobEvent> _events = [];
    private TaskCompletionSource _newEventSignal =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public required AggregationJob Job { get; init; }
    public required DateTimeOffset QueuedAt { get; init; }

    public AggregationJobState State { get; set; } = AggregationJobState.Queued;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public int QueuePosition { get; set; }
    public string? CurrentPartition { get; set; }
    public long BarsEmitted { get; set; }

    public AggregationResult? Result { get; set; }
    public ProgressEvent.Error? Error { get; set; }

    /// <summary>Terminal cancellation reason; populated only when <see cref="State"/> is <see cref="AggregationJobState.Cancelled"/>.</summary>
    public string? CancellationReason { get; set; }

    /// <summary>
    /// Per-job cancellation token source linked into the worker's stopping token at dequeue.
    /// Disposed when the job reaches a terminal state.
    /// </summary>
    public CancellationTokenSource Cts { get; } = new();

    /// <summary>
    /// Snapshot of events with sequence numbers strictly greater than <paramref name="afterSeq"/>.
    /// Pass <c>0</c> for all events. The returned list is detached from internal state.
    /// </summary>
    public IReadOnlyList<JobEvent> EventsAfter(int afterSeq)
    {
        lock (_eventsLock)
        {
            if (afterSeq <= 0)
                return _events.ToArray();
            // Events are appended in monotonic sequence order; scan backward and stop at the cut.
            var result = new List<JobEvent>();
            for (var i = _events.Count - 1; i >= 0; i--)
            {
                if (_events[i].Sequence <= afterSeq) break;
                result.Add(_events[i]);
            }
            result.Reverse();
            return result;
        }
    }

    /// <summary>
    /// Task that completes when <see cref="AppendEvent"/> next fires. Each append swaps in a
    /// fresh signal, so re-read for subsequent events.
    /// </summary>
    public Task NextEventSignal => Volatile.Read(ref _newEventSignal).Task;

    public int LastSequence
    {
        get { lock (_eventsLock) return _events.Count == 0 ? 0 : _events[^1].Sequence; }
    }

    internal void AppendEvent(ProgressEvent ev)
    {
        lock (_eventsLock)
        {
            var seq = _events.Count + 1;
            _events.Add(new JobEvent(seq, ev));
        }

        var prev = Interlocked.Exchange(
            ref _newEventSignal,
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        prev.TrySetResult();
    }

    /// <summary>
    /// Atomic terminal-state transition. Populates Result/Error/CompletedAt under the events
    /// lock BEFORE flipping <see cref="State"/>, so concurrent snapshots cannot observe
    /// (State==Complete &amp;&amp; Result==null) or (State==Error &amp;&amp; Error==null).
    /// </summary>
    internal void MarkTerminal(
        AggregationJobState state,
        DateTimeOffset completedAt,
        AggregationResult? result,
        ProgressEvent.Error? error,
        long barsEmitted,
        ProgressEvent terminalEvent,
        string? cancellationReason = null)
    {
        lock (_eventsLock)
        {
            CompletedAt = completedAt;
            Result = result;
            Error = error;
            BarsEmitted = barsEmitted;
            CancellationReason = cancellationReason;
            State = state;   // visibility anchor — set last while holding the snapshot lock
            var seq = _events.Count + 1;
            _events.Add(new JobEvent(seq, terminalEvent));
        }

        var prev = Interlocked.Exchange(
            ref _newEventSignal,
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        prev.TrySetResult();

        // Release the per-job CTS — terminal records can be retained for retention window
        // and further cancel calls are no-ops.
        Cts.Dispose();
    }

    /// <summary>
    /// Immutable snapshot for API responses. Takes the events lock so State / Result / Error /
    /// CompletedAt are read consistently against a concurrent <see cref="MarkTerminal"/>.
    /// </summary>
    public AggregationJobSnapshot Snapshot()
    {
        lock (_eventsLock)
        {
            return new AggregationJobSnapshot(
                JobId: Job.JobId,
                FeedId: Job.OutcomeFeedId,
                State: State,
                QueuedAt: QueuedAt,
                StartedAt: StartedAt,
                CompletedAt: CompletedAt,
                QueuePosition: QueuePosition,
                CurrentPartition: CurrentPartition,
                BarsEmitted: BarsEmitted,
                Result: Result,
                Error: Error,
                CancellationReason: CancellationReason);
        }
    }
}

public readonly record struct JobEvent(int Sequence, ProgressEvent Event);

public sealed record AggregationJobSnapshot(
    string JobId,
    string FeedId,
    AggregationJobState State,
    DateTimeOffset QueuedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    int QueuePosition,
    string? CurrentPartition,
    long BarsEmitted,
    AggregationResult? Result,
    ProgressEvent.Error? Error,
    string? CancellationReason);
