using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Aggregation;
using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.LiveHost.Application.Live.DataPlane;

public sealed class TickAggregationBarSource : ITickDrivenBarSource
{
    private readonly IBarAccumulator _acc;
    private readonly Action<Int64Bar, bool> _onBar;
    private readonly Queue<Int64Bar> _recent;
    private readonly int _recentCapacity;
    private readonly Lock _gate = new(); // guards _recent: Recent reads on the snapshot/request thread, Emit mutates on the publish/pump thread.

    public TickAggregationBarSource(
        string typeCode, long frozenThreshold, ScaleContext scale, Action<Int64Bar, bool> onBar, int recentCapacity = 256)
    {
        ArgumentNullException.ThrowIfNull(onBar);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(recentCapacity);

        // Source==accumulator scale: live ticks already carry the instrument's scale (frozen at session start).
        _acc = AccumulatorEntry.Open(typeCode, frozenThreshold, scale, scale, DataFeedKind.Tick);
        _onBar = onBar;
        _recentCapacity = recentCapacity;
        _recent = new Queue<Int64Bar>(recentCapacity);
    }

    public IReadOnlyList<Int64Bar> Recent
    {
        get { lock (_gate) return _recent.ToArray(); }
    }

    public void Feed(in TradeTick tick)
    {
        var rec = TickToSourceRecord.From(in tick);
        if (_acc.TryAdvance(in rec, out var bar))
            Emit(ToInt64Bar(in bar));

        while (_acc.TryDrainQueued(out var extra)) // Renko emits multiple bricks per advance.
            Emit(ToInt64Bar(in extra));
    }

    private void Emit(Int64Bar bar)
    {
        lock (_gate)
        {
            if (_recent.Count >= _recentCapacity) _recent.Dequeue();
            _recent.Enqueue(bar);
        }
        _onBar(bar, false); // isStart: tick-aggregation has no bar-open signal — only completed bars
    }

    private static Int64Bar ToInt64Bar(in AggregatedBar b) =>
        new(b.TsMs, b.Open, b.High, b.Low, b.Close, b.Volume);
}
