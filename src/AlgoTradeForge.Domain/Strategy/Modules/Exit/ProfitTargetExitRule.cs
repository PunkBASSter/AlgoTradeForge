using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy.Modules.TradeRegistry;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Strategy.Modules.Exit;

/// <summary>
/// Exits position when unrealized PnL reaches N × ATR.
/// Returns -60 when target reached, 0 otherwise.
/// </summary>
public sealed class ProfitTargetExitRule(double atrMultiple) : IExitRule
{
    public string Name => "ProfitTarget";

    public int Evaluate(Int64Bar bar, StrategyContext context, OrderGroup group)
    {
        if (context.CurrentAtr == 0 || group.EntryPrice == 0)
            return 0;

        var unrealizedPnl = group.EntrySide == OrderSide.Buy
            ? bar.Close - group.EntryPrice
            : group.EntryPrice - bar.Close;

        var target = (long)(atrMultiple * context.CurrentAtr);
        return unrealizedPnl >= target ? -60 : 0;
    }
}
