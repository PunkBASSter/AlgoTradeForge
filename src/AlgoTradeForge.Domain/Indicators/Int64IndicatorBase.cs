using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Domain.Indicators;

public abstract class Int64IndicatorBase : IndicatorBase<long>, IIndicator<Int64Bar, long>
{
    public virtual IndicatorMeasure Measure => IndicatorMeasure.Price;
    public abstract void Compute(IReadOnlyList<Int64Bar> series);
}
