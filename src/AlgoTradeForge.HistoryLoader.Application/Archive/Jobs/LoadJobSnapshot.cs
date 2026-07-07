namespace AlgoTradeForge.HistoryLoader.Application.Archive.Jobs;

public sealed record LoadJobSnapshot(
    string JobId, string State, DateTimeOffset QueuedAt, DateTimeOffset? CompletedAt,
    int MonthsDone, int MonthsTotal, string? CurrentMonth, string? ErrorCode, string? ErrorMessage,
    string Symbol, string FeedName, string Interval, DateOnly From, DateOnly To);
