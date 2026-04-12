using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy.Modules.Regime;

namespace AlgoTradeForge.Domain.Strategy.Modules;

public sealed class StrategyContext
{
    public Int64Bar CurrentBar { get; private set; }
    public DataSubscription CurrentSubscription { get; private set; } = null!;

    public long Equity { get; private set; }
    public long Cash { get; private set; }

    public MarketRegime CurrentRegime { get; internal set; } = MarketRegime.Unknown;

    public long CurrentAtr { get; set; }
    public double CurrentVolatility { get; set; }

    private readonly Dictionary<string, object> _data = [];

    public void Set<T>(string key, T value) => _data[key] = value!;

    public T? Get<T>(string key) =>
        _data.TryGetValue(key, out var v) ? (T)v : default;

    public bool Has(string key) => _data.ContainsKey(key);

    internal void Update(Int64Bar bar, DataSubscription subscription, IOrderContext orders)
    {
        CurrentBar = bar;
        CurrentSubscription = subscription;
        Cash = orders.Cash;
        Equity = Cash + orders.UsedMargin;
    }
}
