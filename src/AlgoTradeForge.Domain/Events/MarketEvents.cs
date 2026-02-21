namespace AlgoTradeForge.Domain.Events;

public sealed record BarEvent(
    DateTimeOffset Timestamp,
    string Source,
    string AssetName,
    string TimeFrame,
    long Open,
    long High,
    long Low,
    long Close,
    long Volume,
    bool IsExportable) : ISubscriptionBoundEvent
{
    public static string TypeId => "bar";
    public static ExportMode DefaultExportMode => ExportMode.Backtest;
}

public sealed record BarMutationEvent(
    DateTimeOffset Timestamp,
    string Source,
    string AssetName,
    string TimeFrame,
    long Open,
    long High,
    long Low,
    long Close,
    long Volume,
    bool IsExportable) : ISubscriptionBoundEvent
{
    public static string TypeId => "bar.mut";
    public static ExportMode DefaultExportMode => ExportMode.Backtest;
}

public sealed record TickEvent(
    DateTimeOffset Timestamp,
    string Source,
    string AssetName,
    long Price,
    decimal Quantity) : IBacktestEvent
{
    public static string TypeId => "tick";
    public static ExportMode DefaultExportMode => ExportMode.Backtest;
}
