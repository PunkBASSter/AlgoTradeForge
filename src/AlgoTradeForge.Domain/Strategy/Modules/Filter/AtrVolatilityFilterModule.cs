using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Optimization.Attributes;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Strategy.Modules.Filter;

[ModuleKey("filter.atr-volatility")]
public sealed class AtrVolatilityFilterModule(AtrVolatilityFilterParams parameters)
    : IStrategyModule<AtrVolatilityFilterParams>, IFilterModule
{
    internal Atr? _indicator;

    public void Initialize(IIndicatorFactory factory, DataSubscription subscription)
    {
        _indicator = new Atr(parameters.Period);
        factory.Create(_indicator, subscription);
    }

    public void Update(IReadOnlyList<Int64Bar> barHistory)
    {
        _indicator?.Compute(barHistory);
    }

    public int Evaluate(Int64Bar bar, OrderSide proposedSide)
    {
        if (_indicator is null)
            return 0;

        var values = _indicator.Buffers["Value"];
        if (values.Count == 0)
            return 0;

        var currentAtr = values[^1];

        if (currentAtr == 0)
            return 0;

        if (parameters.MinAtr > 0 && currentAtr < parameters.MinAtr)
            return 0;

        if (parameters.MaxAtr > 0 && currentAtr > parameters.MaxAtr)
            return 0;

        return 100;
    }
}
