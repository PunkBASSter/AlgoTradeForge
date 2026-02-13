using AlgoTradeForge.Domain.Strategy;

namespace AlgoTradeForge.Domain.History;

public sealed class MarketDataSnapshot
{
    private readonly Dictionary<DataSubscription, TimeSeries<Int64Bar>> _data;

    public MarketDataSnapshot(Dictionary<DataSubscription, TimeSeries<Int64Bar>> data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Count == 0)
            throw new ArgumentException("MarketDataSnapshot requires at least one subscription.", nameof(data));

        _data = data;
    }

    public TimeSeries<Int64Bar> this[DataSubscription subscription] => _data[subscription];

    public bool TryGet(DataSubscription subscription, out TimeSeries<Int64Bar>? series)
        => _data.TryGetValue(subscription, out series);

    public IReadOnlyCollection<DataSubscription> Subscriptions => _data.Keys;
}
