using System.Threading;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Aggregation;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.LiveHost.Application.Live.Recovery;

namespace AlgoTradeForge.LiveHost.Application.Live.DataPlane;

public sealed class TickAggregationBarSource : ITickDrivenBarSource
{
    private enum Phase { Cold, CatchingUp, Live }

    private readonly IBarAccumulator _acc;
    private readonly Action<Int64Bar, bool> _onBar;
    private readonly Queue<Int64Bar> _recent;
    private readonly int _recentCapacity;
    private readonly Lock _gate = new(); // guards _recent: Recent reads on the snapshot/request thread, Emit mutates on the publish/pump thread.
    private readonly CatchupPlan? _catchup;
    private readonly ICatchupGate _watermark = new SequenceWatermarkGate();

    // Live ticks that arrive during catch-up are buffered, then drained in order.
    private readonly Queue<TradeTick> _buffer = new();
    private volatile Phase _phase;
    // Written once in Start() before phase→Live; read in Emit() on pump thread. Interlocked for safe cross-thread publication (volatile long is not valid in C#).
    private long _suppressBarsAtOrBefore = long.MinValue;

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

    public async Task Start()
    {
        if (_catchup is null) return;
        _phase = Phase.CatchingUp;

        // 1. Seed Recent with completed warmup bars (read from the persisted alt-bar feed).
        var warmup = await _catchup.WarmupLoader.Load(
            _catchup.AltBarFeed, DateOnly.MinValue, DateOnly.MaxValue);
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
            request, _watermark, _catchup.OnDiscontinuity ?? (_ => { })))
        {
            FeedAccumulator(in tick, replaying: true);
        }

        // 3. Drain live ticks buffered during catch-up through the same watermark, then go live.
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

    public void Feed(in TradeTick tick)
    {
        if (_phase != Phase.Live)
        {
            lock (_gate) { if (_phase != Phase.Live) { _buffer.Enqueue(tick); return; } }
        }
        FeedAccumulator(in tick, replaying: false);
    }

    private void FeedAccumulator(in TradeTick tick, bool replaying)
    {
        // Live ticks pass the watermark (dedupe vs replay); replayed ticks were already admitted
        // by the coordinator's gate, so re-admitting a live tick is the only place dedupe matters.
        if (!replaying && _watermark.Admit(in tick) != TickAdmission.Accept)
            return;

        var rec = TickToSourceRecord.From(in tick);
        if (_acc.TryAdvance(in rec, out var bar))
            Emit(ToInt64Bar(in bar));
        while (_acc.TryDrainQueued(out var extra))
            Emit(ToInt64Bar(in extra));
    }

    private void Emit(Int64Bar bar)
    {
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

    private static IEnumerable<Int64Bar> TakeLast(TimeSeries<Int64Bar> series, int n)
    {
        var count = series.Count;
        var start = count > n ? count - n : 0;
        for (var i = start; i < count; i++) yield return series[i];
    }

    private static Int64Bar ToInt64Bar(in AggregatedBar b) =>
        new(b.TsMs, b.Open, b.High, b.Low, b.Close, b.Volume);
}
