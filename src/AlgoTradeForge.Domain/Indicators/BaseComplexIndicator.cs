namespace AlgoTradeForge.Domain.Indicators;

public abstract class BaseComplexIndicator : BaseIndicator
{
    private readonly List<IIndicator> _innerIndicators = [];
    private bool _resetting;

    public ComplexIndicatorMode Mode { get; init; } = ComplexIndicatorMode.Parallel;

    public IReadOnlyList<IIndicator> InnerIndicators => _innerIndicators;

    public override int NumValuesToInitialize => Mode switch
    {
        ComplexIndicatorMode.Parallel => _innerIndicators.Count > 0
            ? _innerIndicators.Max(i => i.NumValuesToInitialize)
            : 0,
        ComplexIndicatorMode.Sequence => _innerIndicators.Count > 0
            ? _innerIndicators.Sum(i => i.NumValuesToInitialize) - (_innerIndicators.Count - 1)
            : 0,
        _ => throw new InvalidOperationException($"Unknown mode: {Mode}"),
    };

    protected void AddInner(IIndicator indicator)
    {
        _innerIndicators.Add(indicator);
        indicator.Reseted += OnInnerReseted;
    }

    protected void RemoveInner(IIndicator indicator)
    {
        indicator.Reseted -= OnInnerReseted;
        _innerIndicators.Remove(indicator);
    }

    protected override bool CalcIsFormed() => _innerIndicators.Count > 0 && _innerIndicators.All(i => i.IsFormed);

    protected override long? OnProcess(long input)
    {
        var value = input;

        foreach (var indicator in _innerIndicators)
        {
            var result = indicator.Process(Mode == ComplexIndicatorMode.Sequence ? value : input);

            if (result is null)
                return null;

            value = result.Value;
        }

        // Complex indicators return null; consumers read inner values via CurrentValue
        return null;
    }

    protected override void OnReset()
    {
        if (_resetting)
            return;

        _resetting = true;
        try
        {
            foreach (var indicator in _innerIndicators)
                indicator.Reset();
        }
        finally
        {
            _resetting = false;
        }
    }

    private void OnInnerReseted()
    {
        if (!_resetting)
            Reset();
    }
}
