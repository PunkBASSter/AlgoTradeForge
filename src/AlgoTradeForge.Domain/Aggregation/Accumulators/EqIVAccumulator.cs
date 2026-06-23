using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Aggregation;

namespace AlgoTradeForge.Domain.Aggregation.Accumulators;

// Equal-Imbalance accumulator. Accumulates signed buy/sell volume and emits a bar each time
// abs(signed_acc) >= threshold. Two source paths feed it: tick (is_buyer_maker drives signed
// qty) and time-bar proxy (CandleExtJoiningSource splits Volume by taker_buy_vol). The
// accumulator itself is path-agnostic; the pipeline tags the manifest by source kind.
// Sign convention: positive signed_imbalance => buy-aggressive predominance.
internal sealed class EqIVAccumulator : IBarAccumulator
{
    /// <summary>
    /// Sidecar declaration. Static so eligibility/wiring layers can reference the schema
    /// without instantiating the accumulator (e.g. when pre-validating a job's outcome shape).
    /// </summary>
    public static SidecarSchema Schema { get; } = new(
        Header: "ts,signed_imbalance,buy_volume,sell_volume,realized_threshold",
        Columns: ["signed_imbalance", "buy_volume", "sell_volume", "realized_threshold"],
        FidelityMethodTagTickSource: "tick_signed",
        FidelityMethodTagTimeBarSource: "m1_taker_buy_proxy",
        TimeBarJoinMode: CandleExtJoinMode.TakerBuyVolume);

    public SidecarSchema? SidecarSchema => Schema;

    private readonly long _threshold;
    private readonly double _quantityScale;

    private bool _barEmpty = true;
    private long _tsOpen;
    private long _open;
    private long _high;
    private long _low;
    private long _close;
    private long _signedAccLong;
    private long _buyAccLong;
    private long _sellAccLong;
    private long _baseVolumeAcc;

    private long _barsEmitted;
    private double _overshootSum;
    private double _maxOvershoot;

    private SidecarRow _lastSidecarRow;
    private bool _hasLastSidecar;

    public EqIVAccumulator(long threshold, ScaleContext scale)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(threshold);
        _threshold = threshold;
        _quantityScale = (double)scale.QuantityScale;
        if (_quantityScale <= 0d)
            throw new ArgumentException(
                "EqIV requires a positive QuantityScale on the source/accumulator scale context " +
                "(used to back-convert buy/sell long → raw base-asset double for the sidecar).",
                nameof(scale));
    }

    public bool TryAdvance(in SourceRecord r, out AggregatedBar emitted)
    {
        if (_barEmpty)
        {
            _tsOpen = r.TsMs;
            _open = r.Open;
            _high = r.High;
            _low = r.Low;
            _close = r.Close;
            _signedAccLong = 0;
            _buyAccLong = 0;
            _sellAccLong = 0;
            _baseVolumeAcc = 0;
            _barEmpty = false;
        }
        else
        {
            if (r.High > _high) _high = r.High;
            if (r.Low < _low) _low = r.Low;
            _close = r.Close;
        }

        _buyAccLong += r.BuyVolumeLong;
        _sellAccLong += r.SellVolumeLong;
        _signedAccLong += (r.BuyVolumeLong - r.SellVolumeLong);
        _baseVolumeAcc += r.Volume;

        // Math.Abs(long) throws on long.MinValue rather than silently wrapping. The threshold
        // check guarantees we reset well before _signedAccLong could approach 2^63, but the
        // explicit throw beats overflow-to-negative on pathological inputs.
        var absSigned = Math.Abs(_signedAccLong);
        if (absSigned >= _threshold)
        {
            emitted = new AggregatedBar(_tsOpen, _open, _high, _low, _close, _baseVolumeAcc);

            // Side-feed convention: sidecar columns are doubles in raw base-asset units.
            var buyDouble = _buyAccLong / _quantityScale;
            var sellDouble = _sellAccLong / _quantityScale;
            var signedDouble = buyDouble - sellDouble;
            var realized = signedDouble >= 0d ? signedDouble : -signedDouble;
            _lastSidecarRow = new SidecarRow(_tsOpen, signedDouble, buyDouble, sellDouble, realized);
            _hasLastSidecar = true;

            var overshootPct = (double)(absSigned - _threshold) / _threshold * 100d;
            _overshootSum += overshootPct;
            if (overshootPct > _maxOvershoot) _maxOvershoot = overshootPct;
            _barsEmitted++;

            _barEmpty = true;
            return true;
        }

        emitted = default;
        return false;
    }

    public bool TryGetLastSidecarRow(out SidecarRow row)
    {
        if (_hasLastSidecar)
        {
            row = _lastSidecarRow;
            _hasLastSidecar = false;
            return true;
        }

        row = default;
        return false;
    }

    public AggregationStats Complete()
    {
        var mean = _barsEmitted > 0 ? _overshootSum / _barsEmitted : 0d;
        return new AggregationStats(_barsEmitted, mean, _maxOvershoot);
    }
}
