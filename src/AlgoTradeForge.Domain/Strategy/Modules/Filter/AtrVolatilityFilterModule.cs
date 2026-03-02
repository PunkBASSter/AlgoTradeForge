using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Optimization.Attributes;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Strategy.Modules.Filter;

[ModuleKey("filter.atr-volatility")]
public sealed class AtrVolatilityFilterModule(AtrVolatilityFilterParams parameters)
    : IStrategyModule<AtrVolatilityFilterParams>
{
    internal Atr? _indicator;

    public void Initialize(IIndicatorFactory factory, DataSubscription subscription)
    {
        _indicator = new Atr(parameters.Period);
        factory.Create(_indicator, subscription);
    }

    public bool IsAllowed(Int64Bar bar, OrderSide side)
    {
        if (_indicator is null)
            return false;

        var values = _indicator.Buffers["Value"];
        if (values.Count == 0)
            return false;

        var currentAtr = values[^1];

        if (currentAtr == 0)
            return false;

        if (parameters.MinAtr > 0 && currentAtr < parameters.MinAtr)
            return false;

        if (parameters.MaxAtr > 0 && currentAtr > parameters.MaxAtr)
            return false;

        return true;
    }
}
