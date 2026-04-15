namespace AlgoTradeForge.Application.Optimization;

public enum ComputeTaskType
{
    Optimization,
    Validation
}

public enum ComputeTaskStatus
{
    /// <summary>
    /// Queue-level status: task is waiting in the channel. Distinct from <c>OptimizationRunStatus.Enqueued</c>
    /// / <c>ValidationRunStatus.Enqueued</c>, which are DB-level statuses for the placeholder row.
    /// </summary>
    Pending,
    InProgress,
    Completed,
    Failed,
    Cancelled
}

public sealed class ComputeTask
{
    public Guid Id { get; } = Guid.NewGuid();
    public required Guid JobId { get; init; }
    public required ComputeTaskType Type { get; init; }
    public required int DssIndex { get; init; }
    public required Guid RunId { get; init; }
    public required string DssLabel { get; init; }
    public DateTimeOffset EnqueuedAt { get; init; } = DateTimeOffset.UtcNow;

    public ComputeTaskStatus Status { get; set; } = ComputeTaskStatus.Pending;
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Additional context needed by the executor (e.g., command, axes, settings).
    /// Stored as object to avoid coupling ComputeTask to specific command types.
    /// </summary>
    public object? ExecutionContext { get; init; }
}
