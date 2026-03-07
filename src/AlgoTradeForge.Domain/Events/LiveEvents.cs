using AlgoTradeForge.Domain.Live;

namespace AlgoTradeForge.Domain.Events;

public sealed record LiveSessionStartEvent(
    DateTimeOffset Timestamp,
    string Source,
    Guid SessionId,
    string StrategyName,
    string AssetName,
    LiveEventRouting Routing) : IBacktestEvent
{
    public static string TypeId => "live.start";
    public static ExportMode DefaultExportMode => ExportMode.Live;
}

public sealed record LiveSessionStopEvent(
    DateTimeOffset Timestamp,
    string Source,
    Guid SessionId,
    string Reason) : IBacktestEvent
{
    public static string TypeId => "live.stop";
    public static ExportMode DefaultExportMode => ExportMode.Live;
}

public sealed record LiveConnectionEvent(
    DateTimeOffset Timestamp,
    string Source,
    Guid SessionId,
    string StreamName,
    string Status,
    string? Message = null) : IBacktestEvent
{
    public static string TypeId => "live.conn";
    public static ExportMode DefaultExportMode => ExportMode.Live;
}
