using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Domain.Indicators;

public sealed class Sma : Int64IndicatorBase
{
    private readonly int _period;
    private readonly IndicatorBuffer<long> _buffer = new("Value");
    private readonly Dictionary<string, IndicatorBuffer<long>> _buffers;
    private int _lastProcessedIndex = -1;

    public Sma(int period)
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
            if (i < _period - 1)
            {
                _buffer.Set(i, 0L);
                continue;
            }

            long sum = 0;
            for (var j = i - _period + 1; j <= i; j++)
                sum += series[j].Close;

            _buffer.Set(i, sum / _period);
        }

        if (series.Count > 0)
            _lastProcessedIndex = series.Count - 1;
    }
}
