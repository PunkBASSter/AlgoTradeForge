using AlgoTradeForge.Domain.Events;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Strategy;

namespace AlgoTradeForge.Application.Indicators;

public sealed class EmittingIndicatorDecorator<TInp, TBuff>(
    IIndicator<TInp, TBuff> inner,
    IEventBus bus,
    DataSubscription subscription) : IIndicator<TInp, TBuff>
{
    private int _lastSeriesLength = -1;

    public string Name => inner.Name;
    public IndicatorMeasure Measure => inner.Measure;
    public IReadOnlyDictionary<string, IReadOnlyList<TBuff>> Buffers => inner.Buffers;
    public int MinimumHistory => inner.MinimumHistory;
    public int? CapacityLimit => inner.CapacityLimit;

    public void Compute(IReadOnlyList<TInp> series)
    {
        inner.Compute(series);

        var isMutation = series.Count == _lastSeriesLength;
        _lastSeriesLength = series.Count;

        if (series.Count == 0)
            return;

        var source = EventSources.Indicator(inner.Name);
        var values = ExtractLatestValues();

        if (isMutation)
        {
            bus.Emit(new IndicatorMutationEvent(
                DateTimeOffset.UtcNow,
                source,
                inner.Name,
                inner.Measure,
                values,
                subscription.IsExportable));
        }
        else
        {
            bus.Emit(new IndicatorEvent(
                DateTimeOffset.UtcNow,
                source,
                inner.Name,
                inner.Measure,
                values,
                subscription.IsExportable));
        }
    }

    private Dictionary<string, object?> ExtractLatestValues()
    {
        var values = new Dictionary<string, object?>();
        foreach (var (key, buffer) in inner.Buffers)
        {
            values[key] = buffer.Count > 0 ? (object?)buffer[^1] : null;
        }
        return values;
    }
}
