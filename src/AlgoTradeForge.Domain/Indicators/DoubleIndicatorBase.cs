using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Domain.Indicators;

public abstract class DoubleIndicatorBase() : IIndicator<Int64Bar, double>
{
    public virtual string Name => GetType().Name;
    public virtual IndicatorMeasure Measure => IndicatorMeasure.MinusOnePlusOne;
    public abstract IReadOnlyDictionary<string, IndicatorBuffer<double>> Buffers { get; }
    public virtual int MinimumHistory { get; } = 1;
    public virtual int? CapacityLimit { get; } = null;
    public abstract void Compute(IReadOnlyList<Int64Bar> series);
}
