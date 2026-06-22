using AlgoTradeForge.Domain.Aggregation;

namespace AlgoTradeForge.Domain.Aggregation.Accumulators;

// Renko-bar accumulator. 1x neutral bricks (no 2x reversal); each brick is a clean rectangle
// [lastBrickClose, lastBrickClose +/- brick_size] with no wicks. One source tick can trigger
// 0, 1, or many bricks; multi-brick chains emit the first brick via TryAdvance and the
// remainder via TryDrainQueued.
//
// Volume distribution: trigger tick's volume split tick.Volume / N per brick (last brick takes
// remainder). Pending volume from prior no-emit ticks is added to the first brick of the
// chain. Trailing pending volume from final no-emit ticks is discarded at Complete.
//
// Strict-monotonic output ts: Int64Bar.TimestampMs requires strictly increasing timestamps,
// so a multi-brick chain bumps +1 ms per subsequent brick. These bumps are internal and not
// conflated with AggregationStats.MonotonicBumps (a source-side metric).
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

    // Skips the first-record-as-anchor branch so resumed bricks chain off the prior wall.
    public void SeedResumeState(long lastBrickClose)
    {
        _lastBrickClose = lastBrickClose;
        _seeded = true;
        _pendingVolume = 0;
        _lastEmittedTs = 0;
    }

    public bool TryGetResumeState(out long lastBrickClose)
    {
        lastBrickClose = _lastBrickClose;
        return true;
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

            // Volume split preserves Σ brick.vol = pending + r.Volume: brick 0 carries pending
            // + first share; brick n-1 takes the integer remainder; the rest get per-brick share.
            long vol;
            if (i == 0)
                vol = _pendingVolume + (n == 1 ? lastBrickVol : perBrickVol);
            else if (i == n - 1)
                vol = lastBrickVol;
            else
                vol = perBrickVol;

            // Strict-monotonic ts: max(_lastEmittedTs + 1, tick.TsMs).
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

    public AggregationStats Complete()
    {
        // Renko bricks are exactly brick_size by construction — overshoot is always 0% (kept
        // at 0 rather than NaN so manifest fidelity stays well-formed).
        return new AggregationStats(_barsEmitted, MeanOvershootPct: 0d, MaxOvershootPct: 0d);
    }
}
