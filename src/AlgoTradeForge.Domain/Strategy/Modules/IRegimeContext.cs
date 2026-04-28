using AlgoTradeForge.Domain.Strategy.Modules.Regime;

namespace AlgoTradeForge.Domain.Strategy.Modules;

public interface IRegimeContext
{
    MarketRegime CurrentRegime { get; set; }
}
