using AlgoTradeForge.Domain.Strategy.Modules;

namespace AlgoTradeForge.Domain.Strategy.Rsi2MeanReversion;

public sealed class Rsi2Context : StrategyContextBase, IVolatilityContext
{
    public long CurrentVolatility { get; set; }
}
