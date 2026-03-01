using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Domain.Indicators;

// Average True Range using Wilder's smoothing.
// Output buffer "Value" contains ATR in absolute tick units (long).
public sealed class Atr : Int64IndicatorBase
{
    private readonly int _period;
    private readonly IndicatorBuffer<long> _buffer = new("Value");
    private readonly Dictionary<string, IndicatorBuffer<long>> _buffers;

    private long _previousClose;
    private long _currentAtr;
    private int _trCount;
    private long _trSum;
    private int _lastProcessedIndex = -1;
    private bool _isWarmedUp;

    public Atr(int period)
    {
        _period = period;
        _buffers = new Dictionary<string, IndicatorBuffer<long>> { ["Value"] = _buffer };
    }

    public override IReadOnlyDictionary<string, IndicatorBuffer<long>> Buffers => _buffers;
    public override int MinimumHistory => _period;

    public override void Compute(IReadOnlyList<Int64Bar> series)
    {
        for (var i = _buffer.Count; i < series.Count; i++)
            _buffer.Append(0L);

        var startIndex = _lastProcessedIndex + 1;

        for (var i = startIndex; i < series.Count; i++)
        {
            var bar = series[i];

            var tr = i == 0
                ? bar.High - bar.Low
                : TrueRange(bar, _previousClose);

            _previousClose = bar.Close;

            if (!_isWarmedUp)
            {
                _trSum += tr;
                _trCount++;

                if (_trCount >= _period)
                {
                    _currentAtr = _trSum / _period;
                    _isWarmedUp = true;
                }

                _buffer.Set(i, _isWarmedUp ? _currentAtr : 0L);
            }
            else
            {
                _currentAtr = (_currentAtr * (_period - 1) + tr) / _period;
                _buffer.Set(i, _currentAtr);
            }
        }

        if (series.Count > 0)
            _lastProcessedIndex = series.Count - 1;
    }

    private static long TrueRange(Int64Bar bar, long previousClose)
    {
        var highLow = bar.High - bar.Low;
        var highPrevClose = Math.Abs(bar.High - previousClose);
        var lowPrevClose = Math.Abs(bar.Low - previousClose);
        return Math.Max(highLow, Math.Max(highPrevClose, lowPrevClose));
    }
}
