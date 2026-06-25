using System.Threading;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Aggregation;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.LiveHost.Application.Live.Recovery;

namespace AlgoTradeForge.LiveHost.Application.Live.DataPlane;

public sealed class TickAggregationBarSource : ITickDrivenBarSource, IDisposable
{
    private enum Phase { Cold, CatchingUp, Live }

    private readonly IBarAccumulator _acc;
    private readonly Action<Int64Bar, bool> _onBar;
    private readonly Queue<Int64Bar> _recent;
    private readonly int _recentCapacity;
    private readonly Lock _gate = new(); // guards _recent and _buffer.
    private readonly CatchupPlan? _catchup;
    private readonly ICatchupGate _watermark = new SequenceWatermarkGate();

    // Live ticks that arrive during catch-up are buffered, then drained in order.
    private readonly Queue<TradeTick> _buffer = new();
    private volatile Phase _phase;
    // Interlocked for safe cross-thread publication (volatile long is not valid in C#).
    private long _suppressBarsAtOrBefore = long.MinValue;

    // Reconnect recovery.
    private readonly object _recoveryLatch = new();
    // volatile so WaitForRecoveryIdle() reads the latest published task without taking the latch.
    private volatile Task _recovery = Task.CompletedTask;
    // Set in Emit() — records the open ts of the last emitted bar so RunRecovery knows where to re-replay from.
    private long _lastEmittedOpenTs = long.MinValue;

    // Cancelled on Dispose() to interrupt any in-flight reconnect recovery.
    private readonly CancellationTokenSource _lifetimeCts = new();

    public TickAggregationBarSource(
        string typeCode, long frozenThreshold, ScaleContext scale, Action<Int64Bar, bool> onBar,
        int recentCapacity = 256, CatchupPlan? catchup = null)
    {
        ArgumentNullException.ThrowIfNull(onBar);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(recentCapacity);

        // Source==accumulator scale: live ticks already carry the instrument's scale (frozen at session start).
        _acc = AccumulatorEntry.Open(typeCode, frozenThreshold, scale, scale, DataFeedKind.Tick);
        _onBar = onBar;
        _recentCapacity = recentCapacity;
        _recent = new Queue<Int64Bar>(recentCapacity);
        _catchup = catchup;
        _phase = catchup is null ? Phase.Live : Phase.Cold;
    }

    public IReadOnlyList<Int64Bar> Recent { get { lock (_gate) return _recent.ToArray(); } }

    internal bool IsLive => _phase == Phase.Live;

    // Returns the running recovery task (or Task.CompletedTask when idle), awaitable with optional cancellation.
    internal Task WaitForRecoveryIdle(CancellationToken ct = default)
    {
        var t = _recovery;
        return ct.CanBeCanceled ? t.WaitAsync(ct) : t;
    }

    // Explicit bridge so callers holding an IBarSource reference (e.g. TickRouter) invoke the full
    // catch-up path. The ct-overload is the real implementation; callers that hold the concrete type
    // (e.g. tests) should prefer Start(ct) to pass a meaningful cancellation token.
    Task IBarSource.Start() => Start(CancellationToken.None);

    public async Task Start(CancellationToken ct = default)
    {
        if (_catchup is null) return;
        _phase = Phase.CatchingUp;

        // 1. Seed Recent with completed warmup bars (read from the persisted alt-bar feed).
        var warmup = await _catchup.WarmupLoader.Load(
            _catchup.AltBarFeed, DateOnly.MinValue, DateOnly.MaxValue, ct);
        Int64Bar? lastWarmup = null;
        foreach (var bar in TakeLast(warmup, _catchup.WarmupBarCount))
        {
            PushRecent(bar);          // NOT dispatched: predates the session
            lastWarmup = bar;
        }
        Interlocked.Exchange(ref _suppressBarsAtOrBefore, lastWarmup?.TimestampMs ?? long.MinValue);

        // 2. Replay source records from the boundary; suppress re-derived known bars, dispatch new ones.
        var request = _catchup.Request with { FromTs = _suppressBarsAtOrBefore };
        await foreach (var tick in _catchup.Coordinator.StreamFromBoundary(
            request, _watermark, _catchup.OnDiscontinuity ?? (_ => { }), ct))
        {
            FeedAccumulator(in tick, replaying: true);
        }

        // 3. Drain live ticks buffered during catch-up through the same watermark, then go live.
        DrainBufferAndGoLive();
    }

    public void Feed(in TradeTick tick)
    {
        if (_phase != Phase.Live)
        {
            lock (_gate) { if (_phase != Phase.Live) { _buffer.Enqueue(tick); return; } }
        }

        // Live path: check watermark before accumulating. A Gap can trigger recovery.
        var admission = _watermark.Admit(in tick);
        if (admission == TickAdmission.Gap)
        {
            if (_catchup is not null) TriggerRecovery(in tick);
            return;
        }
        if (admission != TickAdmission.Accept) return;

        Accumulate(in tick);
    }

    // Called during replay (replaying:true) and for drained buffered ticks (replaying:false, gaps are dropped).
    private void FeedAccumulator(in TradeTick tick, bool replaying)
    {
        if (!replaying && _watermark.Admit(in tick) != TickAdmission.Accept) return;
        Accumulate(in tick);
    }

    private void Accumulate(in TradeTick tick)
    {
        var rec = TickToSourceRecord.From(in tick);
        if (_acc.TryAdvance(in rec, out var bar))
            Emit(ToInt64Bar(in bar));
        while (_acc.TryDrainQueued(out var extra))
            Emit(ToInt64Bar(in extra));
    }

    // Single-flight: if already CatchingUp, buffer the trigger tick and return (recovery is running).
    // Otherwise, transition to CatchingUp, buffer the trigger, and launch background recovery.
    private void TriggerRecovery(in TradeTick trigger)
    {
        var t = trigger;
        lock (_recoveryLatch)
        {
            if (_phase == Phase.CatchingUp)
            {
                // Already recovering — buffer the tick so it rejoins the ordered stream after replay.
                lock (_gate) _buffer.Enqueue(t);
                return;
            }
            _phase = Phase.CatchingUp;
            // Enqueue the gap tick: after replay bridges the missing seqs this tick is admitted normally.
            lock (_gate) _buffer.Enqueue(t);
            var fromTs = Interlocked.Read(ref _lastEmittedOpenTs);
            var ct = _lifetimeCts.Token;
            _recovery = Task.Run(() => RunRecovery(_catchup!.Request with { FromTs = fromTs }, ct), ct);
        }
    }

    private async Task RunRecovery(ReplayRequest request, CancellationToken ct)
    {
        try
        {
            await foreach (var tick in _catchup!.Coordinator.StreamFromBoundary(
                request, _watermark, _catchup.OnDiscontinuity ?? (_ => { }), ct))
            {
                FeedAccumulator(in tick, replaying: true);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return; // host shutdown — exit cleanly without corrupting state
        }

        // Drain buffered live ticks through the watermark (gaps are dropped), then go live.
        // Mirror Start()'s drain block exactly.
        DrainBufferAndGoLive();
    }

    // Drains _buffer (gaps dropped) and transitions to Live. Called from Start() and RunRecovery().
    private void DrainBufferAndGoLive()
    {
        lock (_gate)
        {
            while (_buffer.Count > 0)
            {
                var t = _buffer.Dequeue();
                FeedAccumulator(in t, replaying: false);
            }
            _phase = Phase.Live;
        }
    }

    private void Emit(Int64Bar bar)
    {
        Interlocked.Exchange(ref _lastEmittedOpenTs, bar.TimestampMs);
        PushRecent(bar);
        if (bar.TimestampMs <= Interlocked.Read(ref _suppressBarsAtOrBefore)) return; // re-derived known bar — do not dispatch
        _onBar(bar, false);
    }

    private void PushRecent(Int64Bar bar)
    {
        lock (_gate)
        {
            if (_recent.Count >= _recentCapacity) _recent.Dequeue();
            _recent.Enqueue(bar);
        }
    }

    public void Dispose()
    {
        if (_lifetimeCts.IsCancellationRequested) return;
        _lifetimeCts.Cancel();
        _lifetimeCts.Dispose();
    }

    private static IEnumerable<Int64Bar> TakeLast(TimeSeries<Int64Bar> series, int n)
    {
        var count = series.Count;
        var start = count > n ? count - n : 0;
        for (var i = start; i < count; i++) yield return series[i];
    }

    private static Int64Bar ToInt64Bar(in AggregatedBar b) =>
        new(b.TsMs, b.Open, b.High, b.Low, b.Close, b.Volume);
}
