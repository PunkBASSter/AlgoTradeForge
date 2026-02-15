using AlgoTradeForge.Domain.Strategy;

namespace AlgoTradeForge.Application.Abstractions;

public interface IStrategyFactory
{
    IInt64BarStrategy Create(string strategyName, IDictionary<string, object>? parameters = null);
}
