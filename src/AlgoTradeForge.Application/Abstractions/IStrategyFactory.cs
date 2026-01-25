using AlgoTradeForge.Domain.Strategy;

namespace AlgoTradeForge.Application.Abstractions;

public interface IStrategyFactory
{
    IBarStrategy Create(string strategyName, IDictionary<string, object>? parameters = null);
}
