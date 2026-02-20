using System.Text.Json.Serialization;

namespace AlgoTradeForge.Domain.Events;

public sealed record RunStartEvent(
    [property: JsonIgnore] DateTimeOffset Timestamp,
    [property: JsonIgnore] string Source,
    string StrategyName,
    string AssetName,
    long InitialCash,
    DateTimeOffset StartTime,
    DateTimeOffset EndTime,
    ExportMode RunMode) : IBacktestEvent
{
    public static string TypeId => "run.start";
    public static ExportMode DefaultExportMode => ExportMode.Backtest | ExportMode.Optimization | ExportMode.Live;
}

public sealed record RunEndEvent(
    [property: JsonIgnore] DateTimeOffset Timestamp,
    [property: JsonIgnore] string Source,
    int TotalBarsProcessed,
    long FinalEquity,
    int TotalFills,
    TimeSpan Duration) : IBacktestEvent
{
    public static string TypeId => "run.end";
    public static ExportMode DefaultExportMode => ExportMode.Backtest | ExportMode.Optimization | ExportMode.Live;
}

public sealed record ErrorEvent(
    [property: JsonIgnore] DateTimeOffset Timestamp,
    [property: JsonIgnore] string Source,
    string Message,
    string? StackTrace) : IBacktestEvent
{
    public static string TypeId => "err";
    public static ExportMode DefaultExportMode => ExportMode.Backtest | ExportMode.Optimization | ExportMode.Live;
}

public sealed record WarningEvent(
    [property: JsonIgnore] DateTimeOffset Timestamp,
    [property: JsonIgnore] string Source,
    string Message) : IBacktestEvent
{
    public static string TypeId => "warn";
    public static ExportMode DefaultExportMode => ExportMode.Backtest | ExportMode.Optimization | ExportMode.Live;
}
