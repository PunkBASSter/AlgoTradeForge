using AlgoTradeForge.Domain.Optimization.Space;

namespace AlgoTradeForge.Application.Abstractions;

public interface IOptimizationSpaceProvider
{
    IOptimizationSpaceDescriptor? GetDescriptor(string strategyName);
    IReadOnlyDictionary<string, IOptimizationSpaceDescriptor> GetAll();
    IReadOnlyDictionary<string, object> GetParameterDefaults(IOptimizationSpaceDescriptor descriptor);
}
