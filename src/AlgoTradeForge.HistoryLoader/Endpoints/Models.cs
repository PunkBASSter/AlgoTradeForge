namespace AlgoTradeForge.HistoryLoader.Endpoints;

internal sealed record BackfillRequest
{
    public required string Symbol { get; init; }
    public string[]? Feeds { get; init; }
    public DateOnly? FromDate { get; init; }
}

internal sealed record BackfillResponse(string Symbol, string[] FeedsQueued, string Message);

internal sealed record FeedStatusSummary(string Name, string Interval, long? LastTimestamp, int GapCount, string Health);

internal sealed record SymbolStatus(string Symbol, string Type, string Exchange, int FeedCount, IReadOnlyList<FeedStatusSummary> Feeds);

internal sealed record StatusResponse(IReadOnlyList<SymbolStatus> Symbols);

internal sealed record FeedStatusDetail(
    string FeedName, string Interval, long? FirstTimestamp, long? LastTimestamp,
    DateTimeOffset? LastRunUtc, long RecordCount, int GapCount, string Health);

internal sealed record SymbolDetailResponse(string Symbol, string Type, string Exchange, bool BackfillRunning, IReadOnlyList<FeedStatusDetail> Feeds);
