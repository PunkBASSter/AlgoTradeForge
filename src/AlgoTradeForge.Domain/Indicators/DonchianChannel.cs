using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Domain.Indicators;

/// <summary>
/// Donchian Channel: Upper = max(high, period), Lower = min(low, period), Middle = midpoint.
/// All outputs in long (price-scaled).
/// </summary>
public sealed class DonchianChannel : Int64IndicatorBase
{
    private readonly int _period;
    private readonly IndicatorBuffer<long> _upper = new("Upper");
    private readonly IndicatorBuffer<long> _lower = new("Lower");
    private readonly IndicatorBuffer<long> _middle = new("Middle");
    private readonly Dictionary<string, IndicatorBuffer<long>> _buffers;
    private int _lastProcessedIndex = -1;

    public DonchianChannel(int period)
    {
        _period = period;
        _buffers = new Dictionary<string, IndicatorBuffer<long>>
        {
            ["Upper"] = _upper,
            ["Lower"] = _lower,
            ["Middle"] = _middle,
        };
        ApplyBufferCapacity();
    }

    public override IReadOnlyDictionary<string, IndicatorBuffer<long>> Buffers => _buffers;
    public override int MinimumHistory => _period;

    public override void Compute(IReadOnlyList<Int64Bar> series)
    {
        // Extend buffers for new bars
        while (_upper.Count < series.Count)
        {
            _upper.Append(0L);
            _lower.Append(0L);
            _middle.Append(0L);
        }

        var startIndex = _lastProcessedIndex + 1;

        for (var i = startIndex; i < series.Count; i++)
        {
            if (i < _period - 1)
            {
                _upper.Set(i, 0L);
                _lower.Set(i, 0L);
                _middle.Set(i, 0L);
                continue;
            }

            var maxHigh = long.MinValue;
            var minLow = long.MaxValue;
            for (var j = i - _period + 1; j <= i; j++)
            {
                if (series[j].High > maxHigh) maxHigh = series[j].High;
                if (series[j].Low < minLow) minLow = series[j].Low;
            }

            _upper.Set(i, maxHigh);
            _lower.Set(i, minLow);
            _middle.Set(i, (maxHigh + minLow) / 2);
        }

        if (series.Count > 0)
            _lastProcessedIndex = series.Count - 1;
    }
}
