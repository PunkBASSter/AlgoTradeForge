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
/// Configuration (BTCUSDT, research-calibrated for ~100 swings/year):
/// keep the ATR window spanning ~1 day of wall time and scale the multiplier
/// by 1/sqrt(barMinutes):
///   1m  → atrPeriod 1440, multiplier 56–64
///   5m  → atrPeriod 288,  multiplier 25–29
///   15m → atrPeriod 96,   multiplier 14–17
///   1h  → atrPeriod 24,   multiplier 7.3–8.3
///   4h  → atrPeriod 14 (floor for ATR stability), multiplier ~3.5–4.2
///   1d  → atrPeriod 14,   multiplier ~1.7–2.0
/// The sqrt scaling is approximate; recalibrate the multiplier against a target
/// swing density for precise parity with the 1m research calibration.
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
