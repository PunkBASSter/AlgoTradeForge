using AlgoTradeForge.Domain.Live;

namespace AlgoTradeForge.WebApi.Contracts;

public sealed record StartLiveSessionRequest
{
    public required string AssetName { get; init; }
    public required string Exchange { get; init; }
    public required string StrategyName { get; init; }
    public required decimal InitialCash { get; init; }
    public string? TimeFrame { get; init; }
    public Dictionary<string, object>? StrategyParameters { get; init; }
    public decimal CommissionPerTrade { get; init; }
    public string[]? EnabledEvents { get; init; }
    public bool PaperTrading { get; init; }
}

public sealed record LiveSessionSubmissionResponse
{
    public required Guid SessionId { get; init; }
}

public sealed record LiveSessionStatusResponse
{
    public required Guid SessionId { get; init; }
    public required string Status { get; init; }
}

public sealed record LiveSessionListResponse
{
    public required IReadOnlyList<LiveSessionStatusResponse> Sessions { get; init; }
}
