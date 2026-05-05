namespace AlgoTradeForge.HistoryLoader.Application.Aggregation.Accumulators;

// Range-bar accumulator. Emits a bar when running (high - low) crosses the price threshold.
// Tick-only — time-bar collapses would distort actual_overshoot_pct. No sidecar:
// realized_range is reconstructible from primary OHLC.
internal sealed class RangeAccumulator : IBarAccumulator
{
    private readonly long _threshold;

    private bool _barEmpty = true;
    private long _tsOpen;
    private long _open;
    private long _runningHigh;
    private long _runningLow;
    private long _close;
    private long _baseVolumeAcc;

    private long _barsEmitted;
    private double _overshootSum;
    private double _maxOvershoot;

    public RangeAccumulator(long threshold)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(threshold);
        _threshold = threshold;
    }

    public bool TryAdvance(in SourceRecord r, out AggregatedBar emitted)
    {
        if (_barEmpty)
        {
            _tsOpen = r.TsMs;
            _open = r.Open;
            _runningHigh = r.High;
            _runningLow = r.Low;
            _close = r.Close;
            _baseVolumeAcc = r.Volume;
            _barEmpty = false;
        }
        else
        {
            if (r.High > _runningHigh) _runningHigh = r.High;
            if (r.Low < _runningLow) _runningLow = r.Low;
            _close = r.Close;
            _baseVolumeAcc += r.Volume;
        }

        var realizedRange = _runningHigh - _runningLow;
        if (realizedRange >= _threshold)
        {
            emitted = new AggregatedBar(_tsOpen, _open, _runningHigh, _runningLow, _close, _baseVolumeAcc);

            var overshootPct = (double)(realizedRange - _threshold) / _threshold * 100d;
            _overshootSum += overshootPct;
            if (overshootPct > _maxOvershoot) _maxOvershoot = overshootPct;
            _barsEmitted++;

            _barEmpty = true;
            return true;
        }

        emitted = default;
        return false;
    }

    public AggregationStats Finalize()
    {
        var mean = _barsEmitted > 0 ? _overshootSum / _barsEmitted : 0d;
        return new AggregationStats(_barsEmitted, mean, _maxOvershoot);
    }
}
