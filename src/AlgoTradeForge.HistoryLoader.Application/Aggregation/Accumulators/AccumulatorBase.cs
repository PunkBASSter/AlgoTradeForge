namespace AlgoTradeForge.HistoryLoader.Application.Aggregation.Accumulators;

/// <summary>
/// Common bar-emission scaffolding for the type-equivalent accumulator family
/// (TRD §6.3: EqV / EqT / EqD). Subclasses contribute one quantity per source record via
/// <see cref="ThresholdContribution"/>; the base class handles OHLC tracking, base-volume
/// summation, threshold detection, overshoot bookkeeping, and bar reset.
/// </summary>
/// <remarks>
/// Two running totals are kept per in-flight bar:
/// <list type="bullet">
///   <item><c>_thresholdAcc</c> — drives emission. Domain-specific
///         (base-volume for EqV, record-count for EqT, quote-volume for EqD).</item>
///   <item><c>_baseVolumeAcc</c> — always the sum of <see cref="SourceRecord.Volume"/>,
///         regardless of accumulator type. This is what populates <see cref="AggregatedBar.Volume"/>
///         so the on-disk shape stays consistent with <c>Int64Bar</c>.</item>
/// </list>
/// Trailing partial bars (records consumed without crossing the threshold) are discarded
/// at <see cref="Finalize"/> — TRD §6.4 requires <c>realized_threshold ≥ N</c>.
/// </remarks>
internal abstract class AccumulatorBase : IBarAccumulator
{
    // Int128 to keep EqD's Close*Volume product safe. For high-volume perps the per-record
    // product can approach 10^14 and the running sum easily wraps a long. EqV/EqT contributions
    // widen implicitly from long. .NET 7+ has first-class Int128 with native arithmetic.
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

    /// <summary>
    /// The per-source-record contribution to the threshold accumulator.
    /// EqV → <c>r.Volume</c>; EqT → <c>1</c>; EqD → <c>(Int128)r.Close * r.Volume</c>.
    /// Returning <see cref="Int128"/> bounds EqD's price-times-quantity product before it can
    /// overflow the running sum.
    /// </summary>
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

            // Overshoot is on the threshold accumulator, not on base volume. Int128→double
            // is direct (lossy at extreme magnitudes — acceptable for telemetry).
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

    public AggregationStats Finalize()
    {
        var mean = _barsEmitted > 0 ? _overshootSum / _barsEmitted : 0d;
        return new AggregationStats(_barsEmitted, mean, _maxOvershoot);
    }
}
