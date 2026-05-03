namespace AlgoTradeForge.HistoryLoader.Application.Aggregation.Accumulators;

/// <summary>
/// Range-bar accumulator (TRD §6.3, Phase 5). Emits a bar when the running
/// <c>high − low</c> spread crosses the configured price threshold. Distinct from EqV/EqT/EqD
/// in that emission is OHLC-delta-driven, not threshold-accumulator-driven, so this class
/// implements <see cref="IBarAccumulator"/> directly rather than extending
/// <see cref="AccumulatorBase"/>.
/// </summary>
/// <remarks>
/// <para>
/// Phase 5 ships <b>tick-only</b> Range (ADR D1: time-bar Range collapses force a one-emit-per-record
/// approximation that distorts <c>actual_overshoot_pct</c>). For tick sources, every record has
/// <c>high == low == close == price</c>, so each tick incrementally expands the bar's running
/// extremes until the spread crosses the threshold.
/// </para>
/// <para>
/// Sign convention (P5-1 ADR D2): emission rule is <c>(running_high − running_low) ≥ range_size</c>.
/// Realized range at emission is reported via overshoot stats; sidecar publication is
/// intentionally absent (ADR D7 — <c>realized_range</c> is reconstructible from primary OHLC).
/// </para>
/// </remarks>
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

            // Overshoot is on the realized range vs threshold (both same long price-tick units).
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
