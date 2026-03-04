using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Domain.Live;

namespace AlgoTradeForge.Application.Live;

public sealed record StartLiveSessionCommand : ICommand<LiveSessionSubmissionDto>
{
    public required string AssetName { get; init; }
    public required string Exchange { get; init; }
    public required string StrategyName { get; init; }
    public required decimal InitialCash { get; init; }
    public string? TimeFrame { get; init; }
    public IDictionary<string, object>? StrategyParameters { get; init; }
    public decimal CommissionPerTrade { get; init; }
    public LiveEventRouting Routing { get; init; } = LiveEventRouting.OnBarComplete | LiveEventRouting.OnTrade;
    public bool PaperTrading { get; init; }
}

public sealed record LiveSessionSubmissionDto
{
    public required Guid SessionId { get; init; }
}
