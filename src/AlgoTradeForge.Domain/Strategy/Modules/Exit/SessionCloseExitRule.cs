using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy.Modules.TradeRegistry;

namespace AlgoTradeForge.Domain.Strategy.Modules.Exit;

/// <summary>
/// Exits position when the current UTC hour matches the configured close hour.
/// Returns -100 during the close hour, 0 otherwise.
/// </summary>
public sealed class SessionCloseExitRule(int closeHourUtc) : IExitRule
{
    public string Name => "SessionClose";

    public int Evaluate(Int64Bar bar, StrategyContext context, OrderGroup group)
    {
        var hour = bar.Timestamp.UtcDateTime.Hour;
        return hour == closeHourUtc ? -100 : 0;
    }
}
