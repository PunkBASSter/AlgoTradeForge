using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Domain.Indicators;

public abstract class DoubleIndicatorBase : IndicatorBase<double>, IIndicator<Int64Bar, double>
{
    public virtual IndicatorMeasure Measure => IndicatorMeasure.MinusOnePlusOne;
    public abstract void Compute(IReadOnlyList<Int64Bar> series);
}
