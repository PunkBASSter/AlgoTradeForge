using System.Text.Json.Serialization;

namespace AlgoTradeForge.Domain.Events;

public sealed record BarEvent(
    [property: JsonIgnore] DateTimeOffset Timestamp,
    [property: JsonIgnore] string Source,
    string AssetName,
    string TimeFrame,
    long Open,
    long High,
    long Low,
    long Close,
    long Volume,
    [property: JsonIgnore] bool IsExportable) : ISubscriptionBoundEvent
{
    public static string TypeId => "bar";
    public static ExportMode DefaultExportMode => ExportMode.Backtest;
}

public sealed record BarMutationEvent(
    [property: JsonIgnore] DateTimeOffset Timestamp,
    [property: JsonIgnore] string Source,
    string AssetName,
    string TimeFrame,
    long Open,
    long High,
    long Low,
    long Close,
    long Volume,
    [property: JsonIgnore] bool IsExportable) : ISubscriptionBoundEvent
{
    public static string TypeId => "bar.mut";
    public static ExportMode DefaultExportMode => ExportMode.Backtest;
}

public sealed record TickEvent(
    [property: JsonIgnore] DateTimeOffset Timestamp,
    [property: JsonIgnore] string Source,
    string AssetName,
    long Price,
    decimal Quantity) : IBacktestEvent
{
    public static string TypeId => "tick";
    public static ExportMode DefaultExportMode => ExportMode.Backtest;
}
