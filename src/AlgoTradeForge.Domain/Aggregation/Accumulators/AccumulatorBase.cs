using AlgoTradeForge.Domain.Aggregation;

namespace AlgoTradeForge.Domain.Aggregation.Accumulators;

// Common bar-emission scaffolding for the EqV / EqT / EqD family. Subclasses contribute one
// quantity per source record via ThresholdContribution; the base handles OHLC tracking,
// base-volume summation, threshold detection, overshoot bookkeeping, and bar reset.
// Trailing partial bars (records consumed without crossing the threshold) are discarded at
// Complete — emitted bars must satisfy realized_threshold >= N.
internal abstract class AccumulatorBase : IBarAccumulator
{
    // Int128 keeps EqD's Close*Volume product safe — per-record values can approach 10^14 on
    // high-volume perps and the running sum easily wraps a long. EqV/EqT contributions widen
    // implicitly from long.
    private readonly Int128 _threshold;

    private bool _barEmpty = true;
    private long _tsOpen;
    private long _open;
    private long _high;
    private long _low;
    private long _close;
    private Int128 _thresholdAcc;
    private long _baseVolumeAcc;

    private long _barsEmitted;
    private double _overshootSum;
    private double _maxOvershoot;

    protected AccumulatorBase(long threshold)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(threshold);
        _threshold = threshold;
    }

    // Per-source-record contribution to the threshold accumulator. Int128 return bounds EqD's
    // price-times-quantity product before it can overflow the running sum.
    protected abstract Int128 ThresholdContribution(in SourceRecord r);

    public bool TryAdvance(in SourceRecord r, out AggregatedBar emitted)
    {
        if (_barEmpty)
        {
            _tsOpen = r.TsMs;
            _open = r.Open;
            _high = r.High;
            _low = r.Low;
            _close = r.Close;
            _thresholdAcc = 0;
            _baseVolumeAcc = 0;
            _barEmpty = false;
        }
        else
        {
            if (r.High > _high) _high = r.High;
            if (r.Low < _low) _low = r.Low;
            _close = r.Close;
        }

        _thresholdAcc += ThresholdContribution(in r);
        _baseVolumeAcc += r.Volume;

        if (_thresholdAcc >= _threshold)
        {
            emitted = new AggregatedBar(_tsOpen, _open, _high, _low, _close, _baseVolumeAcc);

            // Overshoot is on the threshold accumulator, not base volume. Int128→double is
            // lossy at extreme magnitudes — acceptable for telemetry.
            var overshootPct = (double)(_thresholdAcc - _threshold) / (double)_threshold * 100d;
            _overshootSum += overshootPct;
            if (overshootPct > _maxOvershoot) _maxOvershoot = overshootPct;
            _barsEmitted++;

            _barEmpty = true;
            return true;
        }

        emitted = default;
        return false;
    }

    public AggregationStats Complete()
    {
        var mean = _barsEmitted > 0 ? _overshootSum / _barsEmitted : 0d;
        return new AggregationStats(_barsEmitted, mean, _maxOvershoot);
    }
}
