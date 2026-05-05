using AlgoTradeForge.Domain;

namespace AlgoTradeForge.HistoryLoader.Application.Aggregation.Accumulators;

/// <summary>
/// Equal-Dollar-Imbalance accumulator. Accumulates signed buy/sell <i>quote-asset</i> volume
/// (notional dollars) and emits a bar each time <c>abs(signed_dollar_acc) ≥ threshold</c>.
/// Sibling of <see cref="EqIAccumulator"/> (volume-imbalance) and
/// <see cref="EqITAccumulator"/> (tick-count imbalance). Implements Lopez de Prado's
/// <i>Dollar Imbalance Bar</i> from <i>Advances in Financial Machine Learning</i>.
/// </summary>
/// <remarks>
/// <para>
/// Threshold and contributions live in dollar-tick units (i.e. <c>dollars × QuantityScale × ScaleFactor</c>),
/// matching the units produced by <see cref="ThresholdResolver"/>'s
/// <c>quote_asset</c> path: <c>scale.AmountToTicks(absolute × QuantityScale)</c>.
/// </para>
/// <para>
/// Two source-record paths feed the accumulator:
/// <list type="bullet">
///   <item><b>Tick</b> (<c>useTimeBar = false</c>): per-trade <c>BuyVolumeLong</c>/<c>SellVolumeLong</c>
///         arrive in base-asset-tick units. Contribution per record is
///         <c>(BuyVolumeLong − SellVolumeLong) × Close</c>, computed in <see cref="Int128"/>
///         to avoid 64-bit overflow on the per-record product (~10^14 for high-volume perps).
///         Manifest tag: <c>tick_signed_dollar</c>.</item>
///   <item><b>Time-bar</b> (<c>useTimeBar = true</c>): <see cref="CandleExtJoiningSource"/>
///         pre-multiplies <c>taker_buy_quote_vol</c> into <c>BuyVolumeLong</c>/<c>SellVolumeLong</c>
///         in dollar-tick units, so the per-record contribution is just
///         <c>BuyVolumeLong − SellVolumeLong</c> with no Close-multiply.
///         Manifest tag: <c>m1_taker_buy_quote_proxy</c>.</item>
/// </list>
/// </para>
/// <para>
/// Sidecar emission: per emit, a <see cref="SidecarRow"/> carries <c>signed_dollar_imbalance</c>,
/// <c>buy_dollar</c>, <c>sell_dollar</c>, and <c>realized_threshold</c> as <b>doubles</b> in
/// raw quote-asset (dollar) units. The conversion <c>dollar-tick long → dollar double</c>
/// divides by <c>QuantityScale × ScaleFactor</c> (i.e. <c>QuantityScale / TickSize</c>).
/// </para>
/// <para>
/// Sign convention (mirrors EqIV): positive <c>signed_dollar_imbalance</c> ⇒ buy-aggressive
/// notional predominance; negative ⇒ sell-aggressive.
/// </para>
/// </remarks>
internal sealed class EqIDAccumulator : IBarAccumulator
{
    public static SidecarSchema Schema { get; } = new(
        Header: "ts,signed_dollar_imbalance,buy_dollar,sell_dollar,realized_threshold",
        Columns: ["signed_dollar_imbalance", "buy_dollar", "sell_dollar", "realized_threshold"],
        FidelityMethodTagTickSource: "tick_signed_dollar",
        FidelityMethodTagTimeBarSource: "m1_taker_buy_quote_proxy",
        TimeBarJoinMode: CandleExtJoinMode.TakerBuyQuoteVolume);

    public SidecarSchema? SidecarSchema => Schema;

    private readonly Int128 _threshold;
    private readonly bool _useTimeBar;
    private readonly double _dollarTickPerDollar;       // QuantityScale × ScaleFactor (= QuantityScale / TickSize)

    private bool _barEmpty = true;
    private long _tsOpen;
    private long _open;
    private long _high;
    private long _low;
    private long _close;
    private Int128 _signedDollarTickAcc;        // BuyDollarTick − SellDollarTick cumulative for the in-flight bar; drives emission
    private Int128 _buyDollarTickAcc;           // For sidecar buy_dollar back-conversion
    private Int128 _sellDollarTickAcc;          // For sidecar sell_dollar back-conversion
    private long _baseVolumeAcc;

    private long _barsEmitted;
    private double _overshootSum;
    private double _maxOvershoot;

    private SidecarRow _lastSidecarRow;
    private bool _hasLastSidecar;

    public EqIDAccumulator(long threshold, ScaleContext scale, bool useTimeBar)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(threshold);
        if (scale.QuantityScale <= 0m)
            throw new ArgumentException(
                "EqID requires a positive QuantityScale on the source/accumulator scale context " +
                "(used to back-convert buy/sell dollar-tick long → raw quote-asset double for the sidecar).",
                nameof(scale));
        if (scale.TickSize <= 0m)
            throw new ArgumentException(
                "EqID requires a positive TickSize on the source/accumulator scale context " +
                "(used to back-convert dollar-tick long → raw dollars for the sidecar).",
                nameof(scale));

        _threshold = threshold;
        _useTimeBar = useTimeBar;
        // dollar-tick = dollar × QuantityScale × ScaleFactor = dollar × QuantityScale / TickSize.
        // Inverse: dollar = dollar-tick × TickSize / QuantityScale.
        _dollarTickPerDollar = (double)(scale.QuantityScale / scale.TickSize);
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
            _signedDollarTickAcc = 0;
            _buyDollarTickAcc = 0;
            _sellDollarTickAcc = 0;
            _baseVolumeAcc = 0;
            _barEmpty = false;
        }
        else
        {
            if (r.High > _high) _high = r.High;
            if (r.Low < _low) _low = r.Low;
            _close = r.Close;
        }

        // Per-record dollar-tick contribution. Tick path: raw qty × close. Time-bar path:
        // joiner has already pre-multiplied to dollar-tick units.
        Int128 buyDollarTick;
        Int128 sellDollarTick;
        if (_useTimeBar)
        {
            buyDollarTick = r.BuyVolumeLong;
            sellDollarTick = r.SellVolumeLong;
        }
        else
        {
            // Int128 cast bounds the per-record product: BuyVolumeLong (~10^7 for a typical
            // tick) × Close (~10^7 for a tick-scaled price) lands ~10^14, comfortably inside
            // Int128 but a hair from long.MaxValue (~9.2×10^18) on extreme assets.
            buyDollarTick = (Int128)r.BuyVolumeLong * r.Close;
            sellDollarTick = (Int128)r.SellVolumeLong * r.Close;
        }

        _buyDollarTickAcc += buyDollarTick;
        _sellDollarTickAcc += sellDollarTick;
        _signedDollarTickAcc += buyDollarTick - sellDollarTick;
        _baseVolumeAcc += r.Volume;

        var absSigned = _signedDollarTickAcc >= 0 ? _signedDollarTickAcc : -_signedDollarTickAcc;
        if (absSigned >= _threshold)
        {
            emitted = new AggregatedBar(_tsOpen, _open, _high, _low, _close, _baseVolumeAcc);

            // Side-feed convention: sidecar columns are doubles in raw dollar units.
            var buyDouble = (double)_buyDollarTickAcc / _dollarTickPerDollar;
            var sellDouble = (double)_sellDollarTickAcc / _dollarTickPerDollar;
            var signedDouble = buyDouble - sellDouble;
            var realized = signedDouble >= 0d ? signedDouble : -signedDouble;
            _lastSidecarRow = new SidecarRow(_tsOpen, signedDouble, buyDouble, sellDouble, realized);
            _hasLastSidecar = true;

            // Overshoot is on the absolute signed accumulator vs the threshold (both same units).
            var overshootPct = (double)(absSigned - _threshold) / (double)_threshold * 100d;
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

    public AggregationStats Finalize()
    {
        var mean = _barsEmitted > 0 ? _overshootSum / _barsEmitted : 0d;
        return new AggregationStats(_barsEmitted, mean, _maxOvershoot);
    }
}
