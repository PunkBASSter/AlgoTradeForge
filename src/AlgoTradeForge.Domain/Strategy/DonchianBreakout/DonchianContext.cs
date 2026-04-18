using AlgoTradeForge.Domain.Strategy.Modules;
using AlgoTradeForge.Domain.Strategy.Modules.Regime;

namespace AlgoTradeForge.Domain.Strategy.DonchianBreakout;

public sealed class DonchianContext : StrategyContextBase, IVolatilityContext, IRegimeContext
{
    public long CurrentVolatility { get; set; }
    public MarketRegime CurrentRegime { get; set; } = MarketRegime.Unknown;
}
