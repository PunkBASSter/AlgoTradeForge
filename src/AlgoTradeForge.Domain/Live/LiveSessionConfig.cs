using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Subscriptions;

namespace AlgoTradeForge.Domain.Live;

public sealed record LiveSessionConfig
{
    public required Guid SessionId { get; init; }
    public required IInt64BarStrategy Strategy { get; init; }
    public required IReadOnlyList<DataFeedSubscription> Subscriptions { get; init; }

    public required long InitialCash { get; init; }
    public required string AccountName { get; init; }
    public Asset ExecutionAsset => Subscriptions.ResolveExecutionAsset();
}
