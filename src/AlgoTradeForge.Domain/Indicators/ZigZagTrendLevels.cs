namespace AlgoTradeForge.Domain.Indicators;

/// <summary>
/// N-level breakthrough trend state shared by the zigzag-with-trend indicators
/// (<see cref="DeltaZigZagTrend"/>, <see cref="AtrZigZagTrend"/>): confirmed swing
/// extremes are recorded into fixed-size level arrays (newest at [0]), and the trend
/// flips only when an in-progress extremum breaks beyond the best of the recorded
/// opposite extremes. Trend reads 0 until both arrays are full.
/// </summary>
internal sealed class ZigZagTrendLevels
{
    private readonly int _numberOfLevels;
    private readonly long[] _maxLevels;
    private readonly long[] _minLevels;
    private int _maxLevelCount;
    private int _minLevelCount;
    private bool _upTrend;

    public ZigZagTrendLevels(int numberOfLevels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(numberOfLevels, 1);

        _numberOfLevels = numberOfLevels;
        _maxLevels = new long[numberOfLevels];
        _minLevels = new long[numberOfLevels];
    }

    /// <summary>Records a confirmed swing high.</summary>
    public void RecordHigh(long value) => AddLevel(_maxLevels, ref _maxLevelCount, value);

    /// <summary>Records a confirmed swing low.</summary>
    public void RecordLow(long value) => AddLevel(_minLevels, ref _minLevelCount, value);

    /// <summary>Evaluates the trend; call every bar with the running extremes
    /// (the in-progress extremum on the active side, the last swing on the other).</summary>
    public void Update(long highValue, long lowValue)
    {
        if (!_upTrend && _maxLevelCount > 0 && highValue > ArrayMax(_maxLevels, _maxLevelCount))
            _upTrend = true;
        else if (_upTrend && _minLevelCount > 0 && lowValue < ArrayMin(_minLevels, _minLevelCount))
            _upTrend = false;
    }

    /// <summary>+1/-1 once both level arrays are full, 0 during warmup.</summary>
    public long Trend => _maxLevelCount >= _numberOfLevels && _minLevelCount >= _numberOfLevels
        ? (_upTrend ? 1L : -1L)
        : 0L;

    /// <summary>Best recorded swing high, 0 while none recorded.</summary>
    public long BreakoutHigh => _maxLevelCount > 0 ? ArrayMax(_maxLevels, _maxLevelCount) : 0L;

    /// <summary>Best recorded swing low, 0 while none recorded.</summary>
    public long BreakoutLow => _minLevelCount > 0 ? ArrayMin(_minLevels, _minLevelCount) : 0L;

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
