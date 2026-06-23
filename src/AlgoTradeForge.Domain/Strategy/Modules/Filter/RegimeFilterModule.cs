using AlgoTradeForge.Domain.Strategy.Subscriptions;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Optimization.Attributes;
using AlgoTradeForge.Domain.Strategy.Modules.Regime;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Strategy.Modules.Filter;

/// <summary>
/// Filters entries based on market regime.
/// Returns 100 if current regime is in allowed set, -100 if not, 0 if Unknown.
/// </summary>
[ModuleKey("filter.regime")]
public sealed class RegimeFilterModule : IFilterModule
{
    private readonly IRegimeContext _regimeContext;
    private readonly HashSet<MarketRegime> _allowedRegimes;

    public RegimeFilterModule(IRegimeContext regimeContext, params MarketRegime[] allowedRegimes)
    {
        _regimeContext = regimeContext;
        _allowedRegimes = new HashSet<MarketRegime>(allowedRegimes);
    }

    public void Initialize(IIndicatorFactory factory, DataFeedSubscription subscription)
    {
        // No indicators needed — reads from context
    }

    public int Evaluate(Int64Bar bar, OrderSide proposedSide)
    {
        if (_regimeContext.CurrentRegime == MarketRegime.Unknown)
            return 0;

        return _allowedRegimes.Contains(_regimeContext.CurrentRegime) ? 100 : -100;
    }
}
