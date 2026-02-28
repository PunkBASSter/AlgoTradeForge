using AlgoTradeForge.Domain.Events;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Strategy;

namespace AlgoTradeForge.Application.Indicators;

public sealed class EmittingIndicatorDecorator<TInp, TBuff>(
    IIndicator<TInp, TBuff> inner,
    IEventBus bus,
    DataSubscription subscription) : IIndicator<TInp, TBuff>
{
    private int _lastSeriesLength = -1;
    private bool _hooked;
    private List<(string BufferName, int Index, TBuff Value)>? _pendingMutations;

    public string Name => inner.Name;
    public IndicatorMeasure Measure => inner.Measure;
    public IReadOnlyDictionary<string, IndicatorBuffer<TBuff>> Buffers => inner.Buffers;
    public int MinimumHistory => inner.MinimumHistory;
    public int? CapacityLimit => inner.CapacityLimit;

    public void Compute(IReadOnlyList<TInp> series)
    {
        EnsureHooked();
        _pendingMutations!.Clear();

        inner.Compute(series);

        var isMutation = series.Count == _lastSeriesLength;
        _lastSeriesLength = series.Count;

        if (series.Count == 0)
            return;

        var source = EventSources.Indicator(inner.Name);
        var values = ExtractLatestValues();

        if (values.Count > 0)
        {
            var timestamp = series[^1] is Int64Bar bar ? bar.Timestamp : DateTimeOffset.UtcNow;

            if (isMutation)
            {
                bus.Emit(new IndicatorMutationEvent(
                    timestamp,
                    source,
                    inner.Name,
                    inner.Measure,
                    values,
                    subscription.IsExportable));
            }
            else
            {
                bus.Emit(new IndicatorEvent(
                    timestamp,
                    source,
                    inner.Name,
                    inner.Measure,
                    values,
                    subscription.IsExportable));
            }
        }

        EmitRetroactiveMutations(series, source);
    }

    private void EnsureHooked()
    {
        if (_hooked) return;
        _hooked = true;
        _pendingMutations = [];

        foreach (var (_, buffer) in inner.Buffers)
        {
            buffer.OnRevised = (name, index, value) =>
                _pendingMutations.Add((name, index, value));
        }
    }

    private void EmitRetroactiveMutations(IReadOnlyList<TInp> series, string source)
    {
        if (_pendingMutations is null || _pendingMutations.Count == 0)
            return;

        foreach (var (bufferName, index, value) in _pendingMutations)
        {
            var timestamp = index < series.Count && series[index] is Int64Bar bar
                ? bar.Timestamp
                : DateTimeOffset.UtcNow;

            var buffer = inner.Buffers[bufferName];
            var mutValues = new Dictionary<string, object?>();
            if (buffer.SkipDefaultValues && EqualityComparer<TBuff>.Default.Equals(value, default!))
                mutValues[bufferName] = null;
            else
                mutValues[bufferName] = value;

            bus.Emit(new IndicatorMutationEvent(
                timestamp,
                source,
                inner.Name,
                inner.Measure,
                mutValues,
                subscription.IsExportable));
        }
    }

    private Dictionary<string, object?> ExtractLatestValues()
    {
        var values = new Dictionary<string, object?>();
        foreach (var (key, buffer) in inner.Buffers)
        {
            if (buffer.Count == 0)
                continue;

            var value = buffer[^1];
            if (buffer.SkipDefaultValues && EqualityComparer<TBuff>.Default.Equals(value, default!))
                continue;

            values[key] = value;
        }
        return values;
    }
}
