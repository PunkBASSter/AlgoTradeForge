using AlgoTradeForge.Domain.Optimization.Space;
using AlgoTradeForge.Domain.Strategy;

namespace AlgoTradeForge.Application.Abstractions;

public interface IOptimizationStrategyFactory
{
    IInt64BarStrategy Create(string strategyName, ParameterCombination combination);
}
