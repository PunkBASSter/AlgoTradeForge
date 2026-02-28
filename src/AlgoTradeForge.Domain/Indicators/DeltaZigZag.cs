using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Domain.Indicators;

/// <summary>
/// Dynamic-depth zigzag indicator. Reversal threshold = lastSwingSize * delta,
/// floored at minimumThreshold. Ported from Python onboarding/zigzag/DeltaZigZag.py.
/// </summary>
public sealed class DeltaZigZag : Int64IndicatorBase
{
    private readonly decimal _delta;
    private readonly long _minimumThreshold;

    private readonly IndicatorBuffer<long> _buffer = new("Value", skipDefaultValues: true);
    private readonly Dictionary<string, IndicatorBuffer<long>> _buffers;

    private int _direction = 1;       // 1 = up, -1 = down
    private int _lastPivotIndex;
    private long _currentExtremum;
    private long _lastSwingSize;
    private int _lastProcessedIndex = -1;

    public DeltaZigZag(decimal delta, long minimumThreshold)
    {
        _delta = delta;
        _minimumThreshold = minimumThreshold;
        _buffers = new Dictionary<string, IndicatorBuffer<long>> { ["Value"] = _buffer };
    }

    public override IReadOnlyDictionary<string, IndicatorBuffer<long>> Buffers => _buffers;

    public override void Compute(IReadOnlyList<Int64Bar> series)
    {
        // Extend buffer to match series length
        for (var i = _buffer.Count; i < series.Count; i++)
            _buffer.Append(0L);

        var startIndex = _lastProcessedIndex + 1;

        for (var i = startIndex; i < series.Count; i++)
        {
            var bar = series[i];
            var threshold = GetThreshold();

            if (_direction > 0)
            {
                if (bar.High > _currentExtremum)
                {
                    // Pivot relocates: zero out old pivot, set new one
                    if (_lastPivotIndex != i)
                        _buffer.Revise(_lastPivotIndex, 0L);

                    _currentExtremum = bar.High;
                    _buffer.Set(i, bar.High);
                    _lastPivotIndex = i;
                }
                else if (bar.Low < _currentExtremum - threshold)
                {
                    // Reversal confirmed: record swing, switch direction
                    _lastSwingSize = _currentExtremum - bar.Low;
                    _currentExtremum = bar.Low;
                    _buffer.Set(i, bar.Low);
                    _direction = -1;
                    _lastPivotIndex = i;
                }
            }
            else
            {
                if (bar.Low < _currentExtremum)
                {
                    if (_lastPivotIndex != i)
                        _buffer.Revise(_lastPivotIndex, 0L);

                    _currentExtremum = bar.Low;
                    _buffer.Set(i, bar.Low);
                    _lastPivotIndex = i;
                }
                else if (bar.High > _currentExtremum + threshold)
                {
                    _lastSwingSize = bar.High - _currentExtremum;
                    _currentExtremum = bar.High;
                    _buffer.Set(i, bar.High);
                    _direction = 1;
                    _lastPivotIndex = i;
                }
            }
        }

        if (series.Count > 0)
            _lastProcessedIndex = series.Count - 1;
    }

    private long GetThreshold()
    {
        if (_lastSwingSize == 0)
            return _minimumThreshold;

        var dynamic = (long)(_lastSwingSize * _delta);
        return Math.Max(dynamic, _minimumThreshold);
    }
}
