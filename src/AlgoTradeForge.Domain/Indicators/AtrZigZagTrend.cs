using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Domain.Indicators;

/// <summary>
/// Volatility-adaptive zigzag with N-level breakthrough trend detection:
/// the <see cref="AtrZigZag"/> detector (reversal threshold = multiplier * Wilder ATR
/// pinned at the extremum bar, high-first intrabar convention, bootstrap anchoring;
/// shared via <see cref="AtrZigZagCore"/>) combined with the trend logic of
/// <see cref="DeltaZigZagTrend"/> (shared via <see cref="ZigZagTrendLevels"/>: trend
/// changes only when the in-progress extremum breaks beyond the best of N prior
/// opposite extremes). Buffer names and semantics match <see cref="DeltaZigZagTrend"/>
/// so consumers can use either detector interchangeably; see <see cref="AtrZigZag"/>
/// for the per-timeframe multiplier/period calibration table.
/// </summary>
public sealed class AtrZigZagTrend : Int64IndicatorBase
{
    private readonly AtrZigZagCore _core;
    private readonly ZigZagTrendLevels _levels;

    private readonly IndicatorBuffer<long> _value = new("Value", skipDefaultValues: true);
    private readonly IndicatorBuffer<long> _trend = new("Trend", exportChartId: 1);
    private readonly IndicatorBuffer<long> _breakoutHigh = new("BreakoutHigh");
    private readonly IndicatorBuffer<long> _breakoutLow = new("BreakoutLow");
    private readonly Dictionary<string, IndicatorBuffer<long>> _buffers;

    // Running extremes persist across phases (as in DeltaZigZagTrend): in the up
    // phase _highValue tracks the in-progress high while _lowValue retains the
    // previous swing low, and vice versa.
    private long _highValue;
    private long _lowValue;

    public AtrZigZagTrend(double multiplier, int atrPeriod, int numberOfLevels)
    {
        _core = new AtrZigZagCore(
            multiplier,
            atrPeriod,
            _value,
            onPivotConfirmed: OnPivotConfirmed,
            onBarProcessed: OnBarProcessed);
        _levels = new ZigZagTrendLevels(numberOfLevels);

        _buffers = new Dictionary<string, IndicatorBuffer<long>>
        {
            ["Value"] = _value,
            ["Trend"] = _trend,
            ["BreakoutHigh"] = _breakoutHigh,
            ["BreakoutLow"] = _breakoutLow,
        };
        ApplyBufferCapacity();
    }

    public override int MinimumHistory => _core.MinimumHistory;

    /// <inheritdoc cref="DeltaZigZag.CapacityLimit"/>
    public override int? CapacityLimit => 0;

    public override IReadOnlyDictionary<string, IndicatorBuffer<long>> Buffers => _buffers;

    public override void Compute(IReadOnlyList<Int64Bar> series)
    {
        for (var i = _value.Count; i < series.Count; i++)
        {
            _value.Append(0L);
            _trend.Append(0L);
            _breakoutHigh.Append(0L);
            _breakoutLow.Append(0L);
        }

        _core.Process(series);
    }

    private void OnPivotConfirmed(bool isHigh, long price)
    {
        if (isHigh)
            _levels.RecordHigh(price);
        else
            _levels.RecordLow(price);
    }

    private void OnBarProcessed(int i)
    {
        // Sync the running extreme on the active side; the opposite side keeps the
        // previous swing value. Direction 0 (warmup/bootstrap) leaves both untouched.
        if (_core.Direction > 0)
            _highValue = _core.ExtPrice;
        else if (_core.Direction < 0)
            _lowValue = _core.ExtPrice;

        // Evaluate trend every bar (uses in-progress extremum)
        _levels.Update(_highValue, _lowValue);

        _trend.Set(i, _levels.Trend);
        _breakoutHigh.Set(i, _levels.BreakoutHigh);
        _breakoutLow.Set(i, _levels.BreakoutLow);
    }
}
