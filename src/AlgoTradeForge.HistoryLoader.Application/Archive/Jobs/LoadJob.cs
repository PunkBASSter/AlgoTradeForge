namespace AlgoTradeForge.HistoryLoader.Application.Archive.Jobs;

public sealed record LoadJob(
    string JobId,
    string Exchange, string Symbol, string AssetType,
    string FeedName, string Interval,
    DateOnly From, DateOnly To);
