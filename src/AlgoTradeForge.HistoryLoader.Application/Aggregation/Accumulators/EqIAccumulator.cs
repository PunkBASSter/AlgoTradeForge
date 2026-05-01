using AlgoTradeForge.Domain;

namespace AlgoTradeForge.HistoryLoader.Application.Aggregation.Accumulators;

/// <summary>
/// Equal-Imbalance accumulator (TRD §6.3, Phase 2b). Accumulates signed buy/sell volume
/// and emits a bar each time <c>abs(signed_acc) ≥ threshold</c>. Distinct from EqV/EqT/EqD
/// in that the threshold accumulator is signed and the emission condition is two-sided.
/// </summary>
/// <remarks>
/// <para>
/// Two source-record paths feed the same accumulator (TRD §3.5):
/// <list type="bullet">
///   <item><b>Tick</b>: <c>is_buyer_maker == 0 → BuyVolumeLong = qty</c>;
///         <c>== 1 → SellVolumeLong = qty</c>. Manifest tag: <c>tick_signed</c>.</item>
///   <item><b>Time-bar proxy</b>: <see cref="CandleExtJoiningSource"/> populates
///         <c>BuyVolumeLong = ToLong(taker_buy_vol_double * QuantityScale)</c> and
///         <c>SellVolumeLong = Volume - BuyVolumeLong</c>. Manifest tag: <c>m1_taker_buy_proxy</c>.</item>
/// </list>
/// The accumulator itself is path-agnostic; the pipeline tags the manifest based on
/// <see cref="DataFeedDescriptor.Kind"/>.
/// </para>
/// <para>
/// Sidecar emission (TRD §3.5 / §3.6): per emit, a <see cref="SidecarRow"/> carries
/// <c>signed_imbalance</c>, <c>buy_volume</c>, <c>sell_volume</c>, and
/// <c>realized_threshold</c> as <b>doubles</b> in raw base-asset units (side-feed convention).
/// Conversion <c>long → double</c> happens here at emit, dividing by
/// <see cref="ScaleContext.QuantityScale"/>.
/// </para>
/// <para>
/// Sign convention (pinned by P2b-7 100%-buy and P2b-8 100%-taker-buy fixtures):
/// positive <c>signed_imbalance</c> ⇒ buy-aggressive predominance; negative ⇒ sell-aggressive.
/// </para>
/// </remarks>
internal sealed class EqIAccumulator : IBarAccumulator
{
    private readonly long _threshold;
    private readonly double _quantityScale;

    private bool _barEmpty = true;
    private long _tsOpen;
    private long _open;
    private long _high;
    private long _low;
    private long _close;
    private long _signedAccLong;        // BuyLong - SellLong cumulative for the in-flight bar; drives emission
    private long _buyAccLong;           // For sidecar buy_volume back-conversion
    private long _sellAccLong;          // For sidecar sell_volume back-conversion
    private long _baseVolumeAcc;

    private long _barsEmitted;
    private double _overshootSum;
    private double _maxOvershoot;

    private SidecarRow _lastSidecarRow;
    private bool _hasLastSidecar;

    public EqIAccumulator(long threshold, ScaleContext scale)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(threshold);
        _threshold = threshold;
        _quantityScale = (double)scale.QuantityScale;
        if (_quantityScale <= 0d)
            throw new ArgumentException(
                "EqI requires a positive QuantityScale on the source/accumulator scale context " +
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

        // EqI contributions. Tick: one of Buy/Sell is the qty, the other is 0. Time-bar (proxy):
        // both are non-zero per record (split of vol).
        _buyAccLong += r.BuyVolumeLong;
        _sellAccLong += r.SellVolumeLong;
        _signedAccLong += (r.BuyVolumeLong - r.SellVolumeLong);
        _baseVolumeAcc += r.Volume;

        // Math.Abs(long) throws on long.MinValue rather than silently wrapping. In practice
        // the threshold check guarantees we emit and reset long before _signedAccLong could
        // approach 2^63, but the explicit throw beats overflow-to-negative if a future caller
        // ever feeds pathological inputs.
        var absSigned = Math.Abs(_signedAccLong);
        if (absSigned >= _threshold)
        {
            emitted = new AggregatedBar(_tsOpen, _open, _high, _low, _close, _baseVolumeAcc);

            // Side-feed convention §3.6: sidecar columns are doubles in raw base-asset units.
            var buyDouble = _buyAccLong / _quantityScale;
            var sellDouble = _sellAccLong / _quantityScale;
            var signedDouble = buyDouble - sellDouble;
            var realized = signedDouble >= 0d ? signedDouble : -signedDouble;
            _lastSidecarRow = new SidecarRow(_tsOpen, signedDouble, buyDouble, sellDouble, realized);
            _hasLastSidecar = true;

            // Overshoot is on the absolute signed accumulator vs the threshold (both same units).
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
            // One-shot — pipeline reads exactly once after a successful TryAdvance emit. Resetting
            // here prevents accidental re-write into a later partition's CSV if the call site
            // misorders.
            _hasLastSidecar = false;
            return true;
        }

        row = default;
        return false;
    }

    public AggregationStats Finalize()
    {
        var mean = _barsEmitted > 0 ? _overshootSum / _barsEmitted : 0d;
        return new AggregationStats(_barsEmitted, mean, _maxOvershoot);
    }
}
