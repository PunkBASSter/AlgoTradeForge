using AlgoTradeForge.Domain.Strategy;

namespace AlgoTradeForge.Domain.Live;

public sealed record LiveSessionConfig
{
    public required Guid SessionId { get; init; }
    public required IInt64BarStrategy Strategy { get; init; }
    public required IList<DataSubscription> Subscriptions { get; init; }
    public required Asset PrimaryAsset { get; init; }
    public required long InitialCash { get; init; }
    public long CommissionPerTrade { get; init; }
    public LiveEventRouting Routing { get; init; } = LiveEventRouting.OnBarComplete | LiveEventRouting.OnTrade;
    public required string AccountName { get; init; }
}
