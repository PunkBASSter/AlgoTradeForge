using AlgoTradeForge.Domain.Strategy.Modules;
using AlgoTradeForge.Domain.Strategy.Modules.Regime;

namespace AlgoTradeForge.Domain.Tests.TestUtilities;

/// <summary>
/// Test-only context that implements all capability interfaces.
/// Use in unit tests for modules that need specific context capabilities.
/// </summary>
internal sealed class TestStrategyContext : StrategyContextBase, IVolatilityContext, IRegimeContext, ICrossAssetContext
{
    public long Current { get; set; }
    public MarketRegime CurrentRegime { get; set; } = MarketRegime.Unknown;
    public double ZScore { get; set; }
    public double HedgeRatio { get; set; }
    public bool IsCointegrated { get; set; }
}
