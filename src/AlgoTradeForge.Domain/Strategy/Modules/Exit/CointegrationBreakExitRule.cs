using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy.Modules.TradeRegistry;

namespace AlgoTradeForge.Domain.Strategy.Modules.Exit;

/// <summary>
/// Exits position when pair cointegration breaks.
/// Reads "crossasset.cointegrated" from context. Returns -100 when false, 0 when true or absent.
/// </summary>
public sealed class CointegrationBreakExitRule : IExitRule
{
    public string Name => "CointegrationBreak";

    public int Evaluate(Int64Bar bar, StrategyContext context, OrderGroup group)
    {
        if (!context.Has("crossasset.cointegrated"))
            return 0;

        var isCointegrated = context.Get<bool>("crossasset.cointegrated");
        return isCointegrated ? 0 : -100;
    }
}
