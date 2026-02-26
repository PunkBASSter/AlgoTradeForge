using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Domain.Indicators;

public abstract class Int64IndicatorBase() : IIndicator<Int64Bar, long>
{
    public virtual string Name => GetType().Name;
    public virtual IndicatorMeasure Measure => IndicatorMeasure.Price;
    public abstract IReadOnlyDictionary<string, IReadOnlyList<long>> Buffers { get; }
    public virtual int MinimumHistory { get; } = 1;
    public virtual int? CapacityLimit { get; } = null;
    public virtual bool SkipZeroValues { get; } = false;
    public abstract void Compute(IReadOnlyList<Int64Bar> series);
}
