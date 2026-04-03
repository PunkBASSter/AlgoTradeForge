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
    private readonly StrategyContext _context;
    private readonly HashSet<MarketRegime> _allowedRegimes;

    public RegimeFilterModule(StrategyContext context, params MarketRegime[] allowedRegimes)
    {
        _context = context;
        _allowedRegimes = new HashSet<MarketRegime>(allowedRegimes);
    }

    public void Initialize(IIndicatorFactory factory, DataSubscription subscription)
    {
        // No indicators needed — reads from context
    }

    public int Evaluate(Int64Bar bar, OrderSide proposedSide)
    {
        if (_context.CurrentRegime == MarketRegime.Unknown)
            return 0;

        return _allowedRegimes.Contains(_context.CurrentRegime) ? 100 : -100;
    }
}
