using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Subscriptions;

namespace AlgoTradeForge.Domain.Live;

public sealed record LiveSessionConfig
{
    public required Guid SessionId { get; init; }
    public required IInt64BarStrategy Strategy { get; init; }
    public required IList<DataSubscription> Subscriptions { get; init; }

    // Typed kinds, 1:1 same-order with Subscriptions; the data plane pairs them positionally.
    public required IReadOnlyList<DataFeedSubscription> RawSubscriptions { get; init; }

    public required Asset PrimaryAsset { get; init; }
    public required long InitialCash { get; init; }
    public required string AccountName { get; init; }
}
