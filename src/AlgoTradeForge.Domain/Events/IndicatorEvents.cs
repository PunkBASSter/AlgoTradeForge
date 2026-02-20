using System.Text.Json.Serialization;
using AlgoTradeForge.Domain.Indicators;

namespace AlgoTradeForge.Domain.Events;

public sealed record IndicatorEvent(
    [property: JsonIgnore] DateTimeOffset Timestamp,
    [property: JsonIgnore] string Source,
    string IndicatorName,
    IndicatorMeasure Measure,
    IReadOnlyDictionary<string, object?> Values,
    [property: JsonIgnore] bool IsExportable) : ISubscriptionBoundEvent
{
    public static string TypeId => "ind";
    public static ExportMode DefaultExportMode => ExportMode.Backtest;
}

public sealed record IndicatorMutationEvent(
    [property: JsonIgnore] DateTimeOffset Timestamp,
    [property: JsonIgnore] string Source,
    string IndicatorName,
    IndicatorMeasure Measure,
    IReadOnlyDictionary<string, object?> Values,
    [property: JsonIgnore] bool IsExportable) : ISubscriptionBoundEvent
{
    public static string TypeId => "ind.mut";
    public static ExportMode DefaultExportMode => ExportMode.Backtest;
}
