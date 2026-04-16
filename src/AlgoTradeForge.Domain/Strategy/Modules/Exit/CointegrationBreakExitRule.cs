using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy.Modules.TradeRegistry;

namespace AlgoTradeForge.Domain.Strategy.Modules.Exit;

/// <summary>
/// Exits position when pair cointegration breaks.
/// Returns -100 when IsCointegrated is false, 0 when true or when context is unavailable.
/// </summary>
public sealed class CointegrationBreakExitRule(ICrossAssetContext crossAssetContext) : IExitRule
{
    public string Name => "CointegrationBreak";

    public int Evaluate(Int64Bar bar, StrategyContextBase context, OrderGroup group)
    {
        return crossAssetContext.IsCointegrated ? 0 : -100;
    }
}
