using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Domain.Live;

namespace AlgoTradeForge.Application.Live;

public sealed record StartLiveSessionCommand : ICommand<LiveSessionSubmissionDto>
{
    public required string StrategyName { get; init; }
    public required decimal InitialCash { get; init; }
    public IDictionary<string, object>? StrategyParameters { get; init; }
    public List<DataSubscriptionDto>? DataSubscriptions { get; init; }
    public LiveEventRouting Routing { get; init; } = LiveEventRouting.OnBarComplete | LiveEventRouting.OnTrade;
    public string AccountName { get; init; } = "paper";
}

public sealed record LiveSessionSubmissionDto
{
    public required Guid SessionId { get; init; }
}
