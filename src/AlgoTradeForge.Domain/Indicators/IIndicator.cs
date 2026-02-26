namespace AlgoTradeForge.Domain.Indicators;

public interface IIndicator<TInp, TBuff>
{
    string Name { get; }
    IndicatorMeasure Measure { get; }
    IReadOnlyDictionary<string, IndicatorBuffer<TBuff>> Buffers { get; }
    int MinimumHistory { get; }
    int? CapacityLimit { get; }
    void Compute(IReadOnlyList<TInp> series);
}
