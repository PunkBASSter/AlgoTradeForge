namespace AlgoTradeForge.Domain.Events;

public enum OrderGroupTransition
{
    EntrySubmitted,
    EntryFilled,
    SlPlaced,
    TpPlaced,
    SlFilled,
    TpFilled,
    EntryCancelled,
    ProtectiveCancelled,
    LiquidationSubmitted,
    LiquidationFilled
}

public sealed record OrderGroupEvent(
    DateTimeOffset Timestamp,
    string Source,
    long GroupId,
    string AssetName,
    OrderGroupTransition Transition,
    long? OrderId,
    long? Price,
    decimal? Quantity,
    string? Tag) : IBacktestEvent
{
    public static string TypeId => "grp";
    public static ExportMode DefaultExportMode => ExportMode.Backtest | ExportMode.Live;
}
