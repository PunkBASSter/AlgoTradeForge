namespace AlgoTradeForge.Domain.Indicators;

public abstract class BaseIndicator : IIndicator
{
    private bool? _isFormed;

    public string Name { get; init; }
    public virtual IndicatorMeasure Measure => IndicatorMeasure.Price;
    public virtual DrawStyle Style => DrawStyle.Line;
    public virtual int NumValuesToInitialize => 1;
    public long? CurrentValue { get; private set; }

    public bool IsFormed
    {
        get => _isFormed ??= CalcIsFormed();
    }

    public event Action? Reseted;

    protected BaseIndicator()
    {
        Name = GetType().Name;
    }

    public long? Process(long input)
    {
        var result = OnProcess(input);
        CurrentValue = result;
        _isFormed = null;
        return result;
    }

    public void Reset()
    {
        _isFormed = null;
        CurrentValue = null;
        OnReset();
        Reseted?.Invoke();
    }

    protected abstract long? OnProcess(long input);

    protected virtual void OnReset() { }

    protected virtual bool CalcIsFormed() => CurrentValue is not null;
}
