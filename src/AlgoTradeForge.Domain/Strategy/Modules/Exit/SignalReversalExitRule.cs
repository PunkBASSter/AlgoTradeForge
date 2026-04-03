using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy.Modules.TradeRegistry;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Strategy.Modules.Exit;

/// <summary>
/// Exits position when the strategy's signal direction has flipped vs entry side.
/// Takes a delegate bound to the strategy's signal logic.
/// Returns -70 when signal reversed, 0 when same direction or no signal.
/// </summary>
public sealed class SignalReversalExitRule(
    Func<Int64Bar, StrategyContext, (int signal, OrderSide side)> signalEvaluator) : IExitRule
{
    public string Name => "SignalReversal";

    public int Evaluate(Int64Bar bar, StrategyContext context, OrderGroup group)
    {
        var (signal, side) = signalEvaluator(bar, context);

        if (signal == 0)
            return 0;

        return side != group.EntrySide ? -70 : 0;
    }
}
