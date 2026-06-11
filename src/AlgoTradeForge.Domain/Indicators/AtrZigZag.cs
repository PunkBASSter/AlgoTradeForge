using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Domain.Indicators;

/// <summary>
/// Volatility-adaptive zigzag: reversal threshold = multiplier * Wilder ATR,
/// with the ATR value pinned at the bar of the current extremum.
/// Port of the ATR_MULTIPLE detector selected by the swing_prediction research
/// (AlgoTradeForge.Research swing_prediction/swing_lib/detector.py) — it showed
/// 2.5–3x more stable swing density than fixed-percent zigzags (CV of yearly
/// swing counts ~0.2 vs ~0.65 on BTCUSDT).
///
/// Intrabar convention is high-first: in the down phase an upward confirmation
/// against the prior extreme is checked BEFORE the bar's low may extend it.
/// The detector state machine lives in <see cref="AtrZigZagCore"/>, shared with
/// <see cref="AtrZigZagTrend"/>.
///
/// Configuration (research-calibrated multiplier per target swing density;
/// swing_prediction zigzag_efficiency §2 bisection sweep on M1 2021–2025, value =
/// median across the 8 research symbols, parentheses = per-symbol spread). Keep
/// the ATR window spanning ~1 day of wall time (floored at 14 bars for ATR
/// stability) and scale the multiplier by 1/sqrt(barMinutes):
///
///   TF    atrPeriod   ~100/yr          ~200/yr          ~400/yr
///   1m    1440        56  (51–64)      42   (37–47)     29  (26–33)
///   5m    288         25  (23–29)      19   (17–21)     13  (12–15)
///   15m   96          14  (13–17)      11   (9.6–12)    7.6 (6.8–8.6)
///   1h    24          7.2 (6.6–8.3)    5.4  (4.8–6.1)   3.8 (3.4–4.3)
///   4h    14          3.6 (3.3–4.2)    2.7  (2.4–3.0)   1.9 (1.7–2.1)
///   1d    14          1.7 (1.5–2.0)    1.25 (1.1–1.4)   0.9 (0.8–1.0)
///
/// The 1d row carries a ~+15% correction over naive sqrt scaling (intraday mean
/// reversion makes daily ranges smaller than sqrt-scaled minute ranges). On 1d
/// bars realized density saturates around ~100–150 swings/year regardless of
/// threshold (a leg needs at least one bar plus confirmation lag), so the 1d
/// 200/400 columns are nominal calibration targets, not achievable densities.
/// The sqrt scaling is approximate; recalibrate the multiplier against a target
/// swing density for precise parity with the M1 research calibration.
/// </summary>
public sealed class AtrZigZag : Int64IndicatorBase
{
    private readonly AtrZigZagCore _core;

    private readonly IndicatorBuffer<long> _buffer = new("Value", skipDefaultValues: true);
    private readonly Dictionary<string, IndicatorBuffer<long>> _buffers;

    public AtrZigZag(double multiplier, int atrPeriod)
    {
        _core = new AtrZigZagCore(multiplier, atrPeriod, _buffer);
        _buffers = new Dictionary<string, IndicatorBuffer<long>> { ["Value"] = _buffer };
        ApplyBufferCapacity();
    }

    public override int MinimumHistory => _core.MinimumHistory;

    /// <inheritdoc cref="DeltaZigZag.CapacityLimit"/>
    public override int? CapacityLimit => 0;

    public override IReadOnlyDictionary<string, IndicatorBuffer<long>> Buffers => _buffers;

    public override void Compute(IReadOnlyList<Int64Bar> series)
    {
        for (var i = _buffer.Count; i < series.Count; i++)
            _buffer.Append(0L);

        _core.Process(series);
    }
}
