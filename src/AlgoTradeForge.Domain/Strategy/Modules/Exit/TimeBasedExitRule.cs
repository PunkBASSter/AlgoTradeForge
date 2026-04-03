using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy.Modules.TradeRegistry;

namespace AlgoTradeForge.Domain.Strategy.Modules.Exit;

/// <summary>
/// Exits position after a configurable number of bars held.
/// Returns -100 when bars held >= MaxHoldBars, 0 otherwise.
/// </summary>
public sealed class TimeBasedExitRule(int maxHoldBars, long barIntervalMs) : IExitRule
{
    public string Name => "TimeBased";

    public int Evaluate(Int64Bar bar, StrategyContext context, OrderGroup group)
    {
        var elapsedMs = bar.TimestampMs - group.CreatedAt.ToUnixTimeMilliseconds();
        var barsHeld = elapsedMs / barIntervalMs;
        return barsHeld >= maxHoldBars ? -100 : 0;
    }
}
