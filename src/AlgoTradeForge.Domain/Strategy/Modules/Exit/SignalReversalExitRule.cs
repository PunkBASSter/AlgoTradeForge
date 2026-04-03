using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy.Modules.TradeRegistry;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Strategy.Modules.Exit;

/// <summary>
/// Exits position when the strategy's signal direction has flipped vs entry side.
/// Takes a delegate bound to the strategy's signal logic (signed score:
/// positive = Buy, negative = Sell).
/// Returns -70 when signal reversed, 0 when same direction or no signal.
/// </summary>
public sealed class SignalReversalExitRule(
    Func<Int64Bar, StrategyContext, int> signalEvaluator) : IExitRule
{
    public string Name => "SignalReversal";

    public int Evaluate(Int64Bar bar, StrategyContext context, OrderGroup group)
    {
        var signal = signalEvaluator(bar, context);

        if (signal == 0)
            return 0;

        var side = signal > 0 ? OrderSide.Buy : OrderSide.Sell;
        return side != group.EntrySide ? -70 : 0;
    }
}
