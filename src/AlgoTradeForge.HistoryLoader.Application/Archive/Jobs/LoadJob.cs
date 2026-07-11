using AlgoTradeForge.HistoryLoader.Application.Collection;

namespace AlgoTradeForge.HistoryLoader.Application.Archive.Jobs;

public sealed record LoadJob(
    string JobId,
    CollectionAsset Asset,
    string FeedName, string Interval,
    DateOnly From, DateOnly To);
