using AlgoTradeForge.Domain.Strategy;

namespace AlgoTradeForge.Application.Abstractions;

public interface IStrategyFactory
{
    IIntBarStrategy Create(string strategyName, IDictionary<string, object>? parameters = null);
}
