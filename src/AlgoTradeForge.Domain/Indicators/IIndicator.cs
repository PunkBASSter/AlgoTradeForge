using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Domain.Indicators;

public interface IIndicator<in TInp, TBuff>
{
    string Name { get; }
    IndicatorMeasure Measure { get; }
    IReadOnlyDictionary<string, IReadOnlyList<TBuff>> Buffers { get; }
    int MinimumHistory { get; }
    int? CapacityLimit { get; }
    void Compute(IReadOnlyList<TInp> series);
}
