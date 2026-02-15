using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Domain.Indicators;

public static class IndicatorExtensions
{
    public static long GetField(this Int64Bar bar, BarField field) => field switch
    {
        BarField.Open => bar.Open,
        BarField.High => bar.High,
        BarField.Low => bar.Low,
        BarField.Close => bar.Close,
        BarField.Volume => bar.Volume,
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, null),
    };

    public static long? Process(this IIndicator indicator, Int64Bar bar, BarField field = BarField.Close)
        => indicator.Process(bar.GetField(field));
}
