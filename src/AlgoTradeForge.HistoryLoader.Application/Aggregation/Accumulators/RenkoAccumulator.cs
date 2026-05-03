namespace AlgoTradeForge.HistoryLoader.Application.Aggregation.Accumulators;

/// <summary>
/// Renko-bar accumulator (TRD §6.3, Phase 5). 1× neutral bricks (no 2× reversal); each brick is
/// a clean rectangle <c>[lastBrickClose, lastBrickClose ± brick_size]</c> with no wicks
/// (ADR P5-1 D3, D4). One source tick can trigger 0, 1, or many bricks; multi-brick chains
/// emit the first brick via <see cref="TryAdvance"/> and the remainder via the
/// <see cref="IBarAccumulator.TryDrainQueued"/> DIM (ADR D6).
/// </summary>
/// <remarks>
/// <para>
/// First call seeds <c>_lastBrickClose = tick.Close</c> and stores the tick's volume as
/// pending (no brick emits since delta is zero). Subsequent ticks measure
/// <c>n = (int)(|tick.Close − _lastBrickClose| / _brickSize)</c> bricks; the per-brick close
/// is computed from <c>_lastBrickClose</c> stepping in the direction of price movement.
/// </para>
/// <para>
/// <b>Volume distribution (ADR D5):</b> the trigger tick's volume is split <c>tick.Volume / N</c>
/// per brick (last brick takes the integer remainder). Pending volume from prior no-emit ticks
/// is added to the first brick of the chain. Conservation invariant:
/// <c>Σ brick.vol = Σ (consumed-into-bricks) tick.vol</c> — i.e. trailing pending volume from
/// the final no-emit ticks is discarded at <see cref="Finalize"/>, mirroring how
/// <see cref="RangeAccumulator"/> drops trailing partial bars (TRD §6.4: emitted bars require
/// realized_threshold ≥ N).
/// </para>
/// <para>
/// <b>Strict-monotonic output ts:</b> <c>Int64Bar.TimestampMs</c> requires strictly increasing
/// timestamps. A multi-brick chain triggered by a single tick would otherwise share the tick's
/// <c>TsMs</c>, so we maintain <c>_lastEmittedTs</c> and bump <c>+1 ms</c> per subsequent brick.
/// The bumps are internal — the existing <see cref="AggregationStats.MonotonicBumps"/> is a
/// source-side metric and we don't conflate the two.
/// </para>
/// <para>
/// <b>No sidecar:</b> per ADR D7, Renko publishes no sidecar in v1. <c>direction</c> is
/// reconstructible from <c>sign(close − open)</c>; wick data is intentionally discarded by the
/// clean-rectangle invariant (D4).
/// </para>
/// </remarks>
internal sealed class RenkoAccumulator : IBarAccumulator
{
    private readonly long _brickSize;
    private readonly Queue<AggregatedBar> _queue = new();

    private bool _seeded;
    private long _lastBrickClose;
    private long _pendingVolume;
    private long _lastEmittedTs;

    private long _barsEmitted;

    public RenkoAccumulator(long brickSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(brickSize);
        _brickSize = brickSize;
    }

    public bool TryAdvance(in SourceRecord r, out AggregatedBar emitted)
    {
        if (!_seeded)
        {
            _lastBrickClose = r.Close;
            _pendingVolume = r.Volume;
            _seeded = true;
            emitted = default;
            return false;
        }

        var delta = r.Close - _lastBrickClose;
        var absDelta = delta >= 0 ? delta : -delta;
        var n = (int)(absDelta / _brickSize);

        if (n == 0)
        {
            _pendingVolume += r.Volume;
            emitted = default;
            return false;
        }

        var direction = delta >= 0 ? 1 : -1;
        var perBrickVol = r.Volume / n;
        var lastBrickVol = r.Volume - perBrickVol * (n - 1);

        AggregatedBar firstBrick = default;
        for (var i = 0; i < n; i++)
        {
            var brickOpen = _lastBrickClose;
            var brickClose = brickOpen + direction * _brickSize;
            var brickHigh = direction > 0 ? brickClose : brickOpen;
            var brickLow = direction > 0 ? brickOpen : brickClose;

            // Volume: brick 0 carries pending + first share; bricks 1..n-2 get a per-brick share;
            // brick n-1 takes the integer remainder (preserves Σ brick.vol = pending + r.Volume).
            long vol;
            if (i == 0)
                vol = _pendingVolume + (n == 1 ? lastBrickVol : perBrickVol);
            else if (i == n - 1)
                vol = lastBrickVol;
            else
                vol = perBrickVol;

            // Strict-monotonic ts: max(_lastEmittedTs + 1, tick.TsMs). First brick of a chain
            // typically lands at the tick's ts; subsequent bricks bump +1 each.
            var nextTs = _lastEmittedTs + 1;
            var ts = r.TsMs > nextTs ? r.TsMs : nextTs;

            var brick = new AggregatedBar(ts, brickOpen, brickHigh, brickLow, brickClose, vol);

            _lastBrickClose = brickClose;
            _lastEmittedTs = ts;
            _barsEmitted++;

            if (i == 0)
                firstBrick = brick;
            else
                _queue.Enqueue(brick);
        }

        // All of pending + r.Volume is now distributed across the chain.
        _pendingVolume = 0;
        emitted = firstBrick;
        return true;
    }

    public bool TryDrainQueued(out AggregatedBar emitted)
    {
        if (_queue.Count > 0)
        {
            emitted = _queue.Dequeue();
            return true;
        }

        emitted = default;
        return false;
    }

    public AggregationStats Finalize()
    {
        // Renko bricks are exactly brick_size by construction — overshoot is always 0%.
        // Keep the field at 0 rather than NaN so manifest fidelity stays well-formed.
        return new AggregationStats(_barsEmitted, MeanOvershootPct: 0d, MaxOvershootPct: 0d);
    }
}
