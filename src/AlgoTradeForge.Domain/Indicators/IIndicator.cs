namespace AlgoTradeForge.Domain.Indicators;

public interface IIndicator
{
    string Name { get; }
    bool IsFormed { get; }
    int NumValuesToInitialize { get; }
    IndicatorMeasure Measure { get; }
    DrawStyle Style { get; }
    long? CurrentValue { get; }

    long? Process(long input);
    void Reset();

    event Action? Reseted;
}
