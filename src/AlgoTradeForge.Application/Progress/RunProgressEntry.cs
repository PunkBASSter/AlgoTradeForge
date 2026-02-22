namespace AlgoTradeForge.Application.Progress;

public sealed record RunProgressEntry
{
    public required Guid Id { get; init; }
    public required RunStatus Status { get; init; }
    public required long Processed { get; init; }
    public required long Failed { get; init; }
    public required long Total { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ErrorStackTrace { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
}
