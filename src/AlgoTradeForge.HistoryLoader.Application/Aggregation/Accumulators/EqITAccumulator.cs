namespace AlgoTradeForge.HistoryLoader.Application.Aggregation.Accumulators;

/// <summary>
/// Equal-Tick-count-Imbalance accumulator. Accumulates signed buy/sell <i>trade counts</i>
/// (each trade contributes ±1 regardless of size) and emits a bar each time
/// <c>abs(signed_count_acc) ≥ threshold</c>. Sibling of <see cref="EqIVAccumulator"/> (volume
/// imbalance) and <see cref="EqIDAccumulator"/> (dollar imbalance). Implements Lopez de Prado's
/// <i>Tick Imbalance Bar</i> from <i>Advances in Financial Machine Learning</i>.
/// </summary>
/// <remarks>
/// <para>
/// Threshold and contributions live in raw count units (no scale conversion — counts are
/// dimensionless). <see cref="ThresholdResolver"/>'s <c>trades</c> path passes the threshold
/// through as-is.
/// </para>
/// <para>
/// Two source-record paths feed the accumulator:
/// <list type="bullet">
///   <item><b>Tick</b> (<c>useTimeBar = false</c>): each tick contributes ±1 (sign of
///         <c>BuyVolumeLong − SellVolumeLong</c>). Tied/zero-volume ticks contribute 0.
///         Manifest tag: <c>tick_signed_count</c>.</item>
///   <item><b>Time-bar</b> (<c>useTimeBar = true</c>): <see cref="CandleExtJoiningSource"/>
///         populates <c>BuyTradeCountLong</c>/<c>SellTradeCountLong</c> from
///         <c>taker_buy_trade_count</c> and <c>trade_count − taker_buy_trade_count</c>.
///         Per-record contribution is <c>BuyTradeCountLong − SellTradeCountLong</c>.
///         Manifest tag: <c>m1_taker_buy_count_proxy</c>.
///         Note: the per-minute <c>taker_buy_trade_count</c> column itself is a proxy
///         (<c>round(trade_count × taker_buy_vol / vol)</c>) computed at ingest time, since
///         Binance kline endpoints don't surface it directly. Surface this caveat to users
///         via <c>AltBarWarnings.TimeBarTibApproximation</c>.</item>
/// </list>
/// </para>
/// <para>
/// Sidecar emission: per emit, a <see cref="SidecarRow"/> carries <c>signed_count_imbalance</c>,
/// <c>buy_trade_count</c>, <c>sell_trade_count</c>, and <c>realized_threshold</c> as
/// <b>doubles</b> in raw count units (no scaling — counts ARE the unit).
/// </para>
/// <para>
/// Sign convention: positive <c>signed_count_imbalance</c> ⇒ more buy-aggressor trades than
/// sell-aggressor; negative ⇒ more sell-aggressors. Surfaces participation imbalance
/// independent of trade size, complementing EqIV's volume-weighted and EqID's notional-weighted
/// views.
/// </para>
/// </remarks>
internal sealed class EqITAccumulator : IBarAccumulator
{
    public static SidecarSchema Schema { get; } = new(
        Header: "ts,signed_count_imbalance,buy_trade_count,sell_trade_count,realized_threshold",
        Columns: ["signed_count_imbalance", "buy_trade_count", "sell_trade_count", "realized_threshold"],
        FidelityMethodTagTickSource: "tick_signed_count",
        FidelityMethodTagTimeBarSource: "m1_taker_buy_count_proxy",
        TimeBarJoinMode: CandleExtJoinMode.TakerBuyTradeCount);

    public SidecarSchema? SidecarSchema => Schema;

    private readonly long _threshold;
    private readonly bool _useTimeBar;

    private bool _barEmpty = true;
    private long _tsOpen;
    private long _open;
    private long _high;
    private long _low;
    private long _close;
    private long _signedCountAcc;       // BuyCount − SellCount cumulative for the in-flight bar; drives emission
    private long _buyCountAcc;          // For sidecar buy_trade_count back-conversion
    private long _sellCountAcc;         // For sidecar sell_trade_count back-conversion
    private long _baseVolumeAcc;

    private long _barsEmitted;
    private double _overshootSum;
    private double _maxOvershoot;

    private SidecarRow _lastSidecarRow;
    private bool _hasLastSidecar;

    public EqITAccumulator(long threshold, bool useTimeBar)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(threshold);
        _threshold = threshold;
        _useTimeBar = useTimeBar;
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
            _signedCountAcc = 0;
            _buyCountAcc = 0;
            _sellCountAcc = 0;
            _baseVolumeAcc = 0;
            _barEmpty = false;
        }
        else
        {
            if (r.High > _high) _high = r.High;
            if (r.Low < _low) _low = r.Low;
            _close = r.Close;
        }

        long buyDelta;
        long sellDelta;
        if (_useTimeBar)
        {
            buyDelta = r.BuyTradeCountLong;
            sellDelta = r.SellTradeCountLong;
        }
        else
        {
            // Tick path: each record is a single trade. Aggressor side determined by which of
            // BuyVolumeLong / SellVolumeLong is non-zero (mirroring EqIV's tick-source convention).
            // A tied/zero record contributes 0 — degenerate ticks shouldn't drive emission.
            var signedQty = r.BuyVolumeLong - r.SellVolumeLong;
            buyDelta = signedQty > 0 ? 1L : 0L;
            sellDelta = signedQty < 0 ? 1L : 0L;
        }

        _buyCountAcc += buyDelta;
        _sellCountAcc += sellDelta;
        _signedCountAcc += buyDelta - sellDelta;
        _baseVolumeAcc += r.Volume;

        var absSigned = Math.Abs(_signedCountAcc);
        if (absSigned >= _threshold)
        {
            emitted = new AggregatedBar(_tsOpen, _open, _high, _low, _close, _baseVolumeAcc);

            // Side-feed convention: sidecar columns are doubles. Counts pass through unscaled.
            var buyDouble = (double)_buyCountAcc;
            var sellDouble = (double)_sellCountAcc;
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

    public AggregationStats Finalize()
    {
        var mean = _barsEmitted > 0 ? _overshootSum / _barsEmitted : 0d;
        return new AggregationStats(_barsEmitted, mean, _maxOvershoot);
    }
}
