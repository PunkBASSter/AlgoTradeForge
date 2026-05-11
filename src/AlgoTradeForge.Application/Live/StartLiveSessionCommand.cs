using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Domain.Live;
using AlgoTradeForge.Domain.Strategy.Subscriptions;

namespace AlgoTradeForge.Application.Live;

public sealed record StartLiveSessionCommand : ICommand<LiveSessionSubmissionDto>
{
    public required string StrategyName { get; init; }
    public required decimal InitialCash { get; init; }
    public IDictionary<string, object>? StrategyParameters { get; init; }
    public IReadOnlyList<DataFeedSubscription>? DataSubscriptions { get; init; }
    public LiveEventRouting Routing { get; init; } = LiveEventRouting.OnBarComplete | LiveEventRouting.OnTrade;
    public string AccountName { get; init; } = "paper";
}

public sealed record LiveSessionSubmissionDto
{
    public required Guid SessionId { get; init; }
}
