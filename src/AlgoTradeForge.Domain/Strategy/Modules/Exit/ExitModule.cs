using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy.Modules.TradeRegistry;

namespace AlgoTradeForge.Domain.Strategy.Modules.Exit;

public sealed class ExitModule : IStrategyModule
{
    private readonly List<IExitRule> _rules = [];

    public void AddRule(IExitRule rule) => _rules.Add(rule);

    public int Evaluate(Int64Bar bar, StrategyContext context, OrderGroup group)
    {
        if (_rules.Count == 0) return 0;

        var worstScore = 0;
        foreach (var rule in _rules)
        {
            var score = rule.Evaluate(bar, context, group);
            if (score < worstScore) worstScore = score;
        }
        return worstScore;
    }
}
