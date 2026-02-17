using AlgoTradeForge.Domain.Optimization.Space;

namespace AlgoTradeForge.Application.Abstractions;

public interface IOptimizationSpaceProvider
{
    IOptimizationSpaceDescriptor? GetDescriptor(string strategyName);
}
