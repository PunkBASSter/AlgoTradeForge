using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Domain.Indicators;

/// <summary>
/// Fixed-percentage zigzag with N-level breakthrough trend detection.
/// Faithful port of MQL5 "Delta ZigZag with trend detection" (Rone, 2012).
/// Reversal threshold = reversalPct% of the current swing extremum price.
/// Trend changes only when price breaks beyond the best of N prior opposite extremes.
/// </summary>
public sealed class DeltaZigZagTrend : Int64IndicatorBase
{
    private readonly double _reversalPct;
    private readonly int _numberOfLevels;

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

    // Trend state
    private readonly long[] _maxLevels;
    private readonly long[] _minLevels;
    private int _maxLevelCount;
    private int _minLevelCount;
    private bool _upTrend;

    public DeltaZigZagTrend(double reversalPct, int numberOfLevels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(reversalPct);
        ArgumentOutOfRangeException.ThrowIfLessThan(numberOfLevels, 1);

        _reversalPct = reversalPct;
        _numberOfLevels = numberOfLevels;
        _maxLevels = new long[numberOfLevels];
        _minLevels = new long[numberOfLevels];

        _buffers = new Dictionary<string, IndicatorBuffer<long>>
        {
            ["Value"] = _value,
            ["Trend"] = _trend,
            ["BreakoutHigh"] = _breakoutHigh,
            ["BreakoutLow"] = _breakoutLow,
        };
    }

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
                    AddLevel(_maxLevels, ref _maxLevelCount, _highValue);
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
                    AddLevel(_minLevels, ref _minLevelCount, _lowValue);
                    _highValue = bar.High;
                    _value.Set(i, bar.High);
                    _highBar = i;
                    _up = true;
                }
            }

            // Evaluate trend every bar (uses in-progress extremum)
            UpdateTrend();

            // Write trend and breakout buffers
            bool warmedUp = _maxLevelCount >= _numberOfLevels && _minLevelCount >= _numberOfLevels;
            _trend.Set(i, warmedUp ? (_upTrend ? 1L : -1L) : 0L);
            _breakoutHigh.Set(i, _maxLevelCount > 0 ? ArrayMax(_maxLevels, _maxLevelCount) : 0L);
            _breakoutLow.Set(i, _minLevelCount > 0 ? ArrayMin(_minLevels, _minLevelCount) : 0L);
        }

        if (series.Count > 0)
            _lastProcessedIndex = series.Count - 1;
    }

    private long GetReversal(long extremumPrice) =>
        (long)(extremumPrice * _reversalPct / 100.0);

    private void UpdateTrend()
    {
        if (!_upTrend && _maxLevelCount > 0 && _highValue > ArrayMax(_maxLevels, _maxLevelCount))
            _upTrend = true;
        else if (_upTrend && _minLevelCount > 0 && _lowValue < ArrayMin(_minLevels, _minLevelCount))
            _upTrend = false;
    }

    private void AddLevel(long[] levels, ref int count, long value)
    {
        // Right-shift: newest at [0], oldest falls off end
        var limit = Math.Min(count, _numberOfLevels - 1);
        for (var j = limit; j > 0; j--)
            levels[j] = levels[j - 1];

        levels[0] = value;

        if (count < _numberOfLevels)
            count++;
    }

    private static long ArrayMax(long[] arr, int count)
    {
        var max = arr[0];
        for (var i = 1; i < count; i++)
            if (arr[i] > max)
                max = arr[i];
        return max;
    }

    private static long ArrayMin(long[] arr, int count)
    {
        var min = arr[0];
        for (var i = 1; i < count; i++)
            if (arr[i] < min)
                min = arr[i];
        return min;
    }
}
