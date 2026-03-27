using AlgoTradeForge.Domain.Reporting;

namespace AlgoTradeForge.Domain.Optimization.Fitness;

public interface IFitnessFunction
{
    double Evaluate(PerformanceMetrics metrics);
}
