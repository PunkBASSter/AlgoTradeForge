using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Domain.Indicators;

/// <summary>
/// Volatility-adaptive zigzag with N-level breakthrough trend detection:
/// the <see cref="AtrZigZag"/> detector (reversal threshold = multiplier * Wilder ATR
/// pinned at the extremum bar, high-first intrabar convention, bootstrap anchoring)
/// combined with the trend logic of <see cref="DeltaZigZagTrend"/> (trend changes only
/// when the in-progress extremum breaks beyond the best of N prior opposite extremes).
/// Buffer names and semantics match <see cref="DeltaZigZagTrend"/> so consumers can use
/// either detector interchangeably; see <see cref="AtrZigZag"/> for the per-timeframe
/// multiplier/period calibration table.
/// </summary>
public sealed class AtrZigZagTrend : Int64IndicatorBase
{
    private readonly double _multiplier;
    private readonly int _atrPeriod;
    private readonly int _numberOfLevels;

    private readonly IndicatorBuffer<long> _value = new("Value", skipDefaultValues: true);
    private readonly IndicatorBuffer<long> _trend = new("Trend", exportChartId: 1);
    private readonly IndicatorBuffer<long> _breakoutHigh = new("BreakoutHigh");
    private readonly IndicatorBuffer<long> _breakoutLow = new("BreakoutLow");
    private readonly Dictionary<string, IndicatorBuffer<long>> _buffers;

    // Wilder ATR state (as in AtrZigZag)
    private double _atr = double.NaN;
    private double _trSeedSum;
    private long _previousClose;
    // ATR per bar index, kept only until bootstrap completes (needed to pin the
    // extremum ATR when the bootstrap anchor lands on a past bar).
    private List<double>? _bootstrapAtrs = [];

    // Detector state (as in AtrZigZag)
    private int _direction;          // 0 = bootstrap (undeclared), 1 = up, -1 = down
    private int _extIdx;
    private long _extPrice;
    private double _extAtr;
    private long _startHigh = long.MinValue;
    private long _startLow = long.MaxValue;
    private int _lastProcessedIndex = -1;

    // Trend state (as in DeltaZigZagTrend): running extremes persist across phases —
    // in the up phase _highValue tracks the in-progress high while _lowValue retains
    // the previous swing low, and vice versa.
    private readonly long[] _maxLevels;
    private readonly long[] _minLevels;
    private int _maxLevelCount;
    private int _minLevelCount;
    private bool _upTrend;
    private long _highValue;
    private long _lowValue;

    public AtrZigZagTrend(double multiplier, int atrPeriod, int numberOfLevels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(multiplier);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(atrPeriod);
        ArgumentOutOfRangeException.ThrowIfLessThan(numberOfLevels, 1);

        _multiplier = multiplier;
        _atrPeriod = atrPeriod;
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
        ApplyBufferCapacity();
    }

    public override int MinimumHistory => _atrPeriod + 1;

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
            UpdateAtr(bar, i);
            _bootstrapAtrs?.Add(_atr);

            if (double.IsNaN(_atr))
            {
                // ATR warmup: only track running extremes for the bootstrap phase
                TrackBootstrapExtremes(bar);
                continue;
            }

            if (_direction == 0)
            {
                Bootstrap(series, bar, i);
                continue;
            }

            if (_direction > 0)
            {
                // Extend-first in the up phase (high-first intrabar convention)
                if (bar.High > _extPrice)
                {
                    if (_extIdx != i)
                        _value.Revise(_extIdx, 0L);

                    _extIdx = i;
                    _extPrice = bar.High;
                    _extAtr = _atr;
                    _highValue = bar.High;
                    _value.Set(i, bar.High);
                }

                if ((double)(_extPrice - bar.Low) >= _multiplier * _extAtr && i - _extIdx >= 1)
                {
                    // Reversal down confirmed: the high pivot stands, start a new low
                    AddLevel(_maxLevels, ref _maxLevelCount, _highValue);
                    _direction = -1;
                    _extIdx = i;
                    _extPrice = bar.Low;
                    _extAtr = _atr;
                    _lowValue = bar.Low;
                    _value.Set(i, bar.Low);
                }
            }
            else
            {
                // Confirm-first in the down phase: an upward reversal against the
                // prior extreme wins over extending the low on the same bar
                if ((double)(bar.High - _extPrice) >= _multiplier * _extAtr && i - _extIdx >= 1)
                {
                    AddLevel(_minLevels, ref _minLevelCount, _lowValue);
                    _direction = 1;
                    _extIdx = i;
                    _extPrice = bar.High;
                    _extAtr = _atr;
                    _highValue = bar.High;
                    _value.Set(i, bar.High);
                }
                else if (bar.Low < _extPrice)
                {
                    if (_extIdx != i)
                        _value.Revise(_extIdx, 0L);

                    _extIdx = i;
                    _extPrice = bar.Low;
                    _extAtr = _atr;
                    _lowValue = bar.Low;
                    _value.Set(i, bar.Low);
                }
            }

            WriteTrendBuffers(i);
        }

        if (series.Count > 0)
            _lastProcessedIndex = series.Count - 1;
    }

    private void WriteTrendBuffers(int i)
    {
        // Evaluate trend every bar (uses in-progress extremum)
        UpdateTrend();

        bool warmedUp = _maxLevelCount >= _numberOfLevels && _minLevelCount >= _numberOfLevels;
        _trend.Set(i, warmedUp ? (_upTrend ? 1L : -1L) : 0L);
        _breakoutHigh.Set(i, _maxLevelCount > 0 ? ArrayMax(_maxLevels, _maxLevelCount) : 0L);
        _breakoutLow.Set(i, _minLevelCount > 0 ? ArrayMin(_minLevels, _minLevelCount) : 0L);
    }

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

    private void Bootstrap(IReadOnlyList<Int64Bar> series, Int64Bar bar, int i)
    {
        // Direction is undeclared: breach multiplier*ATR(current) vs the running
        // min/max over PRIOR bars, then anchor at the true argmax/argmin over [0..i].
        if (_startHigh == long.MinValue)
        {
            // First evaluated bar with no prior extremes (atrPeriod == 1)
            TrackBootstrapExtremes(bar);
            return;
        }

        var threshold = _multiplier * _atr;
        var upMove = (double)(bar.High - _startLow);
        var downMove = (double)(_startHigh - bar.Low);

        if (upMove >= threshold)
        {
            _direction = 1;
            AnchorExtremum(series, i, anchorHigh: true);
        }
        else if (downMove >= threshold)
        {
            _direction = -1;
            AnchorExtremum(series, i, anchorHigh: false);
        }
        else
        {
            TrackBootstrapExtremes(bar);
        }
    }

    private void AnchorExtremum(IReadOnlyList<Int64Bar> series, int i, bool anchorHigh)
    {
        var extIdx = 0;
        var extPrice = anchorHigh ? series[0].High : series[0].Low;
        for (var j = 1; j <= i; j++)
        {
            var p = anchorHigh ? series[j].High : series[j].Low;
            if (anchorHigh ? p > extPrice : p < extPrice)
            {
                extIdx = j;
                extPrice = p;
            }
        }

        _extIdx = extIdx;
        _extPrice = extPrice;
        var anchorAtr = _bootstrapAtrs![extIdx];
        _extAtr = double.IsNaN(anchorAtr) ? _atr : anchorAtr;

        if (anchorHigh)
            _highValue = extPrice;
        else
            _lowValue = extPrice;

        // The anchor usually lands on a past bar: write it through Revise so chart
        // emitters (buffer.OnRevised) see the retroactive pivot — Set is silent.
        if (extIdx == i)
            _value.Set(extIdx, extPrice);
        else
            _value.Revise(extIdx, extPrice);

        _bootstrapAtrs = null; // no longer needed once direction is declared
    }

    private void TrackBootstrapExtremes(Int64Bar bar)
    {
        if (bar.High > _startHigh)
            _startHigh = bar.High;

        if (bar.Low < _startLow)
            _startLow = bar.Low;
    }

    private void UpdateAtr(Int64Bar bar, int i)
    {
        double tr;
        if (i == 0)
        {
            tr = bar.High - bar.Low;
        }
        else
        {
            var highLow = (double)(bar.High - bar.Low);
            var highClose = Math.Abs((double)(bar.High - _previousClose));
            var lowClose = Math.Abs((double)(bar.Low - _previousClose));
            tr = Math.Max(highLow, Math.Max(highClose, lowClose));
        }

        _previousClose = bar.Close;

        if (i < _atrPeriod - 1)
        {
            _trSeedSum += tr;
        }
        else if (i == _atrPeriod - 1)
        {
            // Wilder seed: SMA of the first atrPeriod true ranges
            _atr = (_trSeedSum + tr) / _atrPeriod;
        }
        else
        {
            _atr += (tr - _atr) / _atrPeriod;
        }
    }
}
