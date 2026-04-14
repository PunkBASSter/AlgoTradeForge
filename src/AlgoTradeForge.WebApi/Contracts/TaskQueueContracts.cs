namespace AlgoTradeForge.WebApi.Contracts;

public sealed record TaskQueueItemResponse
{
    public required Guid Id { get; init; }
    public required Guid JobId { get; init; }
    public required string Type { get; init; }
    public required int DssIndex { get; init; }
    public required string DssLabel { get; init; }
    public required Guid RunId { get; init; }
    public required string Status { get; init; }
    public required DateTimeOffset EnqueuedAt { get; init; }
    public TaskProgressDto? Progress { get; init; }
}

public sealed record TaskProgressDto
{
    public required long Processed { get; init; }
    public required long Total { get; init; }
}

public sealed record TaskQueueSnapshotResponse
{
    public required IReadOnlyList<TaskQueueItemResponse> ActiveTasks { get; init; }
    public required int PendingCount { get; init; }
    public Guid? InProgressTask { get; init; }
}

public sealed record CancelTaskResponse
{
    public required Guid TaskId { get; init; }
    public required string Status { get; init; }
    public IReadOnlyList<Guid> CascadeCancelled { get; init; } = [];
}

public sealed record PurgeResponse
{
    public required int PurgedCount { get; init; }
    public IReadOnlyList<Guid> PurgedTaskIds { get; init; } = [];
}
