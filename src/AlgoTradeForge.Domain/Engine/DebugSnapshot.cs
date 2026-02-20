namespace AlgoTradeForge.Domain.Engine;

/// <summary>
/// Immutable snapshot passed to the debug probe at each bar boundary.
/// </summary>
public readonly record struct DebugSnapshot(
    long SequenceNumber,
    long TimestampMs,
    int SubscriptionIndex,
    bool IsExportableSubscription,
    int FillsThisBar,
    long PortfolioEquity);
