namespace AlgoTradeForge.Application;

public sealed record DataSubscriptionDto
{
    public required string AssetName { get; init; }
    public required string Exchange { get; init; }
    public required string TimeFrame { get; init; }
}
