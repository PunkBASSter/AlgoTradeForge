namespace AlgoTradeForge.Domain.Events;

[Flags]
public enum ExportMode
{
    Backtest = 1,
    Optimization = 2,
    Live = 4,
}

public interface IBacktestEvent
{
    DateTimeOffset Timestamp { get; }
    string Source { get; }

    static abstract string TypeId { get; }
    static abstract ExportMode DefaultExportMode { get; }
}

public interface ISubscriptionBoundEvent : IBacktestEvent
{
    bool IsExportable { get; }
}
