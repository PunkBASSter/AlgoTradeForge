namespace AlgoTradeForge.Domain.Indicators;

public class IndicatorBase<TInp, TBuff>(string name, IndicatorMeasure measure,
    IReadOnlyDictionary<string, IReadOnlyList<TBuff>> buffers, int minimumHistory, int? capacityLimit = null)
    : IIndicator<TInp, TBuff>
{
    public string Name { get; } = name;
    public IndicatorMeasure Measure { get; } = measure;
    public IReadOnlyDictionary<string, IReadOnlyList<TBuff>> Buffers { get; } = buffers;
    public int MinimumHistory { get; } = minimumHistory;
    public int? CapacityLimit { get; } = capacityLimit;

    public void Compute(IReadOnlyList<TInp> series)
    {
        throw new NotImplementedException();
    }
}
