using AlgoTradeForge.Domain.Indicators;

namespace AlgoTradeForge.Domain.Events;

public sealed record IndicatorEvent(
    DateTimeOffset Timestamp,
    string Source,
    string IndicatorName,
    IndicatorMeasure Measure,
    IReadOnlyDictionary<string, object?> Values,
    bool IsExportable) : ISubscriptionBoundEvent
{
    public static string TypeId => "ind";
    public static ExportMode DefaultExportMode => ExportMode.Backtest;
}

public sealed record IndicatorMutationEvent(
    DateTimeOffset Timestamp,
    string Source,
    string IndicatorName,
    IndicatorMeasure Measure,
    IReadOnlyDictionary<string, object?> Values,
    bool IsExportable) : ISubscriptionBoundEvent
{
    public static string TypeId => "ind.mut";
    public static ExportMode DefaultExportMode => ExportMode.Backtest;
}
