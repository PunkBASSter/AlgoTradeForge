using AlgoTradeForge.Domain.Engine;

namespace AlgoTradeForge.Application.Debug;

public sealed record DebugSessionDto(
    Guid SessionId,
    string AssetName,
    string StrategyName,
    DateTimeOffset CreatedAt);

public sealed record DebugStepResultDto(
    bool SessionActive,
    long SequenceNumber,
    long TimestampMs,
    int SubscriptionIndex,
    bool IsExportableSubscription,
    int FillsThisBar,
    long PortfolioEquity)
{
    public static DebugStepResultDto From(DebugSnapshot snapshot, bool sessionActive) => new(
        sessionActive,
        snapshot.SequenceNumber,
        snapshot.TimestampMs,
        snapshot.SubscriptionIndex,
        snapshot.IsExportableSubscription,
        snapshot.FillsThisBar,
        snapshot.PortfolioEquity);
}

public sealed record DebugSessionStatusDto(
    Guid SessionId,
    bool IsRunning,
    DebugStepResultDto? LastSnapshot,
    DateTimeOffset CreatedAt);
