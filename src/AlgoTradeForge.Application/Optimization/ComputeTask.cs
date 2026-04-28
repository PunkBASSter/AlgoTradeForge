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

    /// <summary>
    /// Mutable status — written by the consumer thread and by HTTP threads
    /// (via <see cref="ComputeTaskQueue.TryCancelTask"/> / <see cref="ComputeTaskQueue.TryCancelJob"/>).
    /// Uses <see cref="Volatile"/> to ensure cross-thread visibility.
    /// Stored as <c>int</c> because <c>Volatile.Read/Write&lt;T&gt;</c> require reference types.
    /// </summary>
    private int _status = (int)ComputeTaskStatus.Pending;
    public ComputeTaskStatus Status
    {
        get => (ComputeTaskStatus)Volatile.Read(ref _status);
        set => Volatile.Write(ref _status, (int)value);
    }

    private string? _errorMessage;
    public string? ErrorMessage
    {
        get => Volatile.Read(ref _errorMessage);
        set => Volatile.Write(ref _errorMessage, value);
    }

    /// <summary>
    /// Additional context needed by the executor (e.g., command, axes, settings).
    /// Stored as object to avoid coupling ComputeTask to specific command types.
    /// </summary>
    public object? ExecutionContext { get; init; }
}
