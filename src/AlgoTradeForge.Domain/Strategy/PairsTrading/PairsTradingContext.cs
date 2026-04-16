using AlgoTradeForge.Domain.Strategy.Modules;

namespace AlgoTradeForge.Domain.Strategy.PairsTrading;

public sealed class PairsTradingContext : StrategyContextBase, IVolatilityContext, ICrossAssetContext
{
    public long Current { get; set; }
    public double ZScore { get; set; }
    public double HedgeRatio { get; set; }
    public bool IsCointegrated { get; set; }
}
