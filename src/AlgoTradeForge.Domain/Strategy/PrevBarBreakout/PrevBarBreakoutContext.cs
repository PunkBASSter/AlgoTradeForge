using AlgoTradeForge.Domain.Strategy.Modules;

namespace AlgoTradeForge.Domain.Strategy.PrevBarBreakout;

public sealed class PrevBarBreakoutContext : StrategyContextBase
{
    /// <summary>
    /// Latest ATR reading in tick units. Zero until the indicator warms up.
    /// </summary>
    public long CurrentAtr { get; internal set; }
}
