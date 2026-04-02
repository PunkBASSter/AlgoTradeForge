using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Domain.Indicators;

public sealed class Rsi : DoubleIndicatorBase
{
    private readonly int _period;
    private readonly IndicatorBuffer<double> _buffer = new("Value");
    private readonly Dictionary<string, IndicatorBuffer<double>> _buffers;

    private double _avgGain;
    private double _avgLoss;
    private int _lastProcessedIndex = -1;
    private bool _isWarmedUp;
    private double _prevClose;
    private int _warmupCount;

    public Rsi(int period = 14)
    {
        _period = period;
        _buffers = new Dictionary<string, IndicatorBuffer<double>> { ["Value"] = _buffer };
    }

    public override IReadOnlyDictionary<string, IndicatorBuffer<double>> Buffers => _buffers;
    public override int MinimumHistory => _period + 1;

    public override void Compute(IReadOnlyList<Int64Bar> series)
    {
        for (var i = _buffer.Count; i < series.Count; i++)
            _buffer.Append(50.0);

        var startIndex = _lastProcessedIndex + 1;

        for (var i = startIndex; i < series.Count; i++)
        {
            var close = (double)series[i].Close;

            if (i == 0)
            {
                _prevClose = close;
                _buffer.Set(i, 50.0);
                _lastProcessedIndex = i;
                continue;
            }

            var change = close - _prevClose;
            _prevClose = close;

            var gain = change > 0 ? change : 0;
            var loss = change < 0 ? -change : 0;

            if (!_isWarmedUp)
            {
                _avgGain += gain;
                _avgLoss += loss;
                _warmupCount++;

                if (_warmupCount >= _period)
                {
                    _avgGain /= _period;
                    _avgLoss /= _period;
                    _isWarmedUp = true;
                    var rs = _avgLoss == 0 ? double.MaxValue : _avgGain / _avgLoss;
                    _buffer.Set(i, 100.0 - 100.0 / (1.0 + rs));
                }
                else
                {
                    _buffer.Set(i, 50.0);
                }
            }
            else
            {
                _avgGain = (_avgGain * (_period - 1) + gain) / _period;
                _avgLoss = (_avgLoss * (_period - 1) + loss) / _period;
                var rs = _avgLoss == 0 ? double.MaxValue : _avgGain / _avgLoss;
                _buffer.Set(i, 100.0 - 100.0 / (1.0 + rs));
            }
        }

        if (series.Count > 0)
            _lastProcessedIndex = series.Count - 1;
    }
}
