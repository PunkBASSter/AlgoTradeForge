using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Strategy;

namespace AlgoTradeForge.Application.Abstractions;

public interface IStrategyFactory
{
    IInt64BarStrategy Create(string strategyName, IIndicatorFactory indicatorFactory, IDictionary<string, object>? parameters = null);
}
