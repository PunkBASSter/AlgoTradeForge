using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Domain.Indicators;

/// <summary>
/// Fixed-percentage zigzag with N-level breakthrough trend detection.
/// Faithful port of MQL5 "Delta ZigZag with trend detection" (Rone, 2012).
/// Reversal threshold = reversalPct% of the current swing extremum price.
/// Trend changes only when price breaks beyond the best of N prior opposite
/// extremes (level logic shared with <see cref="AtrZigZagTrend"/> via
/// <see cref="ZigZagTrendLevels"/>).
/// </summary>
public sealed class DeltaZigZagTrend : Int64IndicatorBase
{
    private readonly double _reversalPct;
    private readonly ZigZagTrendLevels _levels;

    private readonly IndicatorBuffer<long> _value = new("Value", skipDefaultValues: true);
    private readonly IndicatorBuffer<long> _trend = new("Trend", exportChartId: 1);
    private readonly IndicatorBuffer<long> _breakoutHigh = new("BreakoutHigh");
    private readonly IndicatorBuffer<long> _breakoutLow = new("BreakoutLow");
    private readonly Dictionary<string, IndicatorBuffer<long>> _buffers;

    // Zigzag state
    private bool _up = true;
    private int _highBar, _lowBar;
    private long _highValue, _lowValue;
    private int _lastProcessedIndex = -1;

    public DeltaZigZagTrend(double reversalPct, int numberOfLevels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(reversalPct);

        _reversalPct = reversalPct;
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

        var startIndex = _lastProcessedIndex + 1;

        for (var i = startIndex; i < series.Count; i++)
        {
            var bar = series[i];

            if (_up)
            {
                var reversal = GetReversal(_highValue);

                if (bar.High > _highValue)
                {
                    // Relocate high pivot
                    if (_highBar != i)
                        _value.Revise(_highBar, 0L);

                    _highValue = bar.High;
                    _value.Set(i, bar.High);
                    _highBar = i;
                }
                else if (bar.Low < _highValue - reversal)
                {
                    // Reversal down: record swing high, start new low
                    _levels.RecordHigh(_highValue);
                    _lowValue = bar.Low;
                    _value.Set(i, bar.Low);
                    _lowBar = i;
                    _up = false;
                }
            }
            else
            {
                var reversal = GetReversal(_lowValue);

                if (bar.Low < _lowValue)
                {
                    // Relocate low pivot
                    if (_lowBar != i)
                        _value.Revise(_lowBar, 0L);

                    _lowValue = bar.Low;
                    _value.Set(i, bar.Low);
                    _lowBar = i;
                }
                else if (bar.High > _lowValue + reversal)
                {
                    // Reversal up: record swing low, start new high
                    _levels.RecordLow(_lowValue);
                    _highValue = bar.High;
                    _value.Set(i, bar.High);
                    _highBar = i;
                    _up = true;
                }
            }

            // Evaluate trend every bar (uses in-progress extremum)
            _levels.Update(_highValue, _lowValue);

            _trend.Set(i, _levels.Trend);
            _breakoutHigh.Set(i, _levels.BreakoutHigh);
            _breakoutLow.Set(i, _levels.BreakoutLow);
        }

        if (series.Count > 0)
            _lastProcessedIndex = series.Count - 1;
    }

    private long GetReversal(long extremumPrice) =>
        (long)(extremumPrice * _reversalPct / 100.0);
}
