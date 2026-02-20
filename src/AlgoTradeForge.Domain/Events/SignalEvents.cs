using System.Text.Json.Serialization;

namespace AlgoTradeForge.Domain.Events;

public sealed record SignalEvent(
    [property: JsonIgnore] DateTimeOffset Timestamp,
    [property: JsonIgnore] string Source,
    string SignalName,
    string AssetName,
    string Direction,
    decimal Strength,
    string? Reason) : IBacktestEvent
{
    public static string TypeId => "sig";
    public static ExportMode DefaultExportMode => ExportMode.Backtest | ExportMode.Live;
}

public sealed record RiskEvent(
    [property: JsonIgnore] DateTimeOffset Timestamp,
    [property: JsonIgnore] string Source,
    string AssetName,
    bool Passed,
    string CheckName,
    string? Reason) : IBacktestEvent
{
    public static string TypeId => "risk";
    public static ExportMode DefaultExportMode => ExportMode.Backtest | ExportMode.Live;
}
