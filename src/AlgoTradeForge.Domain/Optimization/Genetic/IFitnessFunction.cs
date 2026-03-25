using AlgoTradeForge.Domain.Reporting;

namespace AlgoTradeForge.Domain.Optimization.Genetic;

public interface IFitnessFunction
{
    double Evaluate(PerformanceMetrics metrics);
}
