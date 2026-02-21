using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Events;

public sealed record OrderPlaceEvent(
    DateTimeOffset Timestamp,
    string Source,
    long OrderId,
    string AssetName,
    OrderSide Side,
    OrderType Type,
    decimal Quantity,
    long? LimitPrice,
    long? StopPrice) : IBacktestEvent
{
    public static string TypeId => "ord.place";
    public static ExportMode DefaultExportMode => ExportMode.Backtest | ExportMode.Optimization | ExportMode.Live;
}

public sealed record OrderFillEvent(
    DateTimeOffset Timestamp,
    string Source,
    long OrderId,
    string AssetName,
    OrderSide Side,
    long Price,
    decimal Quantity,
    long Commission) : IBacktestEvent
{
    public static string TypeId => "ord.fill";
    public static ExportMode DefaultExportMode => ExportMode.Backtest | ExportMode.Optimization | ExportMode.Live;
}

public sealed record OrderCancelEvent(
    DateTimeOffset Timestamp,
    string Source,
    long OrderId,
    string AssetName,
    string? Reason) : IBacktestEvent
{
    public static string TypeId => "ord.cancel";
    public static ExportMode DefaultExportMode => ExportMode.Backtest | ExportMode.Optimization | ExportMode.Live;
}

public sealed record OrderRejectEvent(
    DateTimeOffset Timestamp,
    string Source,
    long OrderId,
    string AssetName,
    string Reason) : IBacktestEvent
{
    public static string TypeId => "ord.reject";
    public static ExportMode DefaultExportMode => ExportMode.Backtest | ExportMode.Optimization | ExportMode.Live;
}

public sealed record PositionEvent(
    DateTimeOffset Timestamp,
    string Source,
    string AssetName,
    decimal Quantity,
    long AverageEntryPrice,
    long RealizedPnl) : IBacktestEvent
{
    public static string TypeId => "pos";
    public static ExportMode DefaultExportMode => ExportMode.Backtest | ExportMode.Optimization | ExportMode.Live;
}
