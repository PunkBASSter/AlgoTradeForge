using System.Collections.Concurrent;
using System.Threading.Channels;
using AlgoTradeForge.Application.Persistence;

namespace AlgoTradeForge.Application.Optimization;

public sealed class ComputeTaskQueue
{
    /// <summary>
    /// Unbounded channel consumed by a single <c>ComputeQueueConsumer</c> instance.
    /// <c>SingleReader = true</c> enables lock-free fast-path reads; do NOT add a second consumer.
    /// </summary>
    private readonly Channel<ComputeTask> _channel = Channel.CreateUnbounded<ComputeTask>(
        new UnboundedChannelOptions { SingleReader = true });

    private readonly ConcurrentDictionary<Guid, ComputeTask> _tasks = new();

    private ComputeTask? _activeTask;

    /// <summary>
    /// The task currently being executed by the consumer.
    /// Read from HTTP threads (GetSnapshot), written from the consumer thread — volatile ensures visibility.
    /// </summary>
    public ComputeTask? ActiveTask
    {
        get => Volatile.Read(ref _activeTask);
        set => Volatile.Write(ref _activeTask, value);
    }

    public ChannelReader<ComputeTask> Reader => _channel.Reader;

    public void Enqueue(ComputeTask task)
    {
        _tasks.TryAdd(task.Id, task);
        _channel.Writer.TryWrite(task);
    }

    public void EnqueueRange(IEnumerable<ComputeTask> tasks)
    {
        foreach (var task in tasks)
            Enqueue(task);
    }

    public bool TryCancelTask(Guid taskId, out ComputeTask? task, out List<ComputeTask> cascadeCancelled)
    {
        cascadeCancelled = [];

        if (!_tasks.TryGetValue(taskId, out task))
            return false;

        if (task.Status is ComputeTaskStatus.Completed or ComputeTaskStatus.Failed or ComputeTaskStatus.Cancelled)
            return false;

        task.Status = ComputeTaskStatus.Cancelled;
        task.ErrorMessage = "Cancelled by user.";

        // Cascade: if this is an optimization task, cancel paired pending validation tasks
        if (task.Type == ComputeTaskType.Optimization)
        {
            foreach (var candidate in _tasks.Values)
            {
                if (candidate.Type == ComputeTaskType.Validation
                    && candidate.JobId == task.JobId
                    && candidate.DssIndex == task.DssIndex
                    && candidate.Status == ComputeTaskStatus.Pending)
                {
                    candidate.Status = ComputeTaskStatus.Cancelled;
                    candidate.ErrorMessage = "Optimization was cancelled.";
                    cascadeCancelled.Add(candidate);
                }
            }
        }

        return true;
    }

    public (int PurgedCount, List<Guid> PurgedIds) PurgePending()
    {
        var purged = new List<Guid>();

        foreach (var task in _tasks.Values)
        {
            if (task.Status == ComputeTaskStatus.Pending)
            {
                task.Status = ComputeTaskStatus.Cancelled;
                task.ErrorMessage = "Purged from queue.";
                purged.Add(task.Id);
            }
        }

        return (purged.Count, purged);
    }

    public IReadOnlyList<ComputeTask> GetSnapshot()
    {
        var result = new List<ComputeTask>();

        // In-progress first
        if (ActiveTask is { Status: ComputeTaskStatus.InProgress })
            result.Add(ActiveTask);

        // Then pending in enqueue order (ConcurrentDictionary doesn't guarantee order,
        // but EnqueuedAt provides ordering)
        result.AddRange(
            _tasks.Values
                .Where(t => t.Status == ComputeTaskStatus.Pending)
                .OrderBy(t => t.EnqueuedAt));

        return result;
    }

    public void RemoveCompleted(Guid taskId)
    {
        _tasks.TryRemove(taskId, out _);
    }

    public void Complete()
    {
        _channel.Writer.TryComplete();
    }

    // ── Job tracking for group status finalization ──

    private readonly ConcurrentDictionary<Guid, JobTracker> _jobTrackers = new();

    public void RegisterJob(Guid jobId, int totalTasks, bool isOptimizationJob, string? groupRunKey = null)
    {
        _jobTrackers[jobId] = new JobTracker(totalTasks, isOptimizationJob, groupRunKey);
    }

    /// <summary>
    /// Record a task completion for its parent job.
    /// Returns (isJobComplete, childStatuses, isOptimizationJob, groupRunKey) when the job finishes.
    /// </summary>
    public (bool IsComplete, List<string>? ChildStatuses, bool IsOptimizationJob, string? GroupRunKey)
        RecordTaskCompletion(ComputeTask task)
    {
        if (!_jobTrackers.TryGetValue(task.JobId, out var tracker))
            return (false, null, false, null);

        var status = task.Status switch
        {
            ComputeTaskStatus.Completed => task.Type == ComputeTaskType.Optimization
                ? OptimizationRunStatus.Completed : ValidationRunStatus.Completed,
            ComputeTaskStatus.Cancelled => task.Type == ComputeTaskType.Optimization
                ? OptimizationRunStatus.Cancelled : ValidationRunStatus.Cancelled,
            _ => task.Type == ComputeTaskType.Optimization
                ? OptimizationRunStatus.Failed : ValidationRunStatus.Failed,
        };

        tracker.ChildStatuses.Add(status);
        var completed = Interlocked.Increment(ref tracker.CompletedCount);

        if (completed < tracker.TotalTasks)
            return (false, null, tracker.IsOptimizationJob, tracker.GroupRunKey);

        _jobTrackers.TryRemove(task.JobId, out _);
        return (true, tracker.ChildStatuses, tracker.IsOptimizationJob, tracker.GroupRunKey);
    }

    private sealed class JobTracker(int totalTasks, bool isOptimizationJob, string? groupRunKey)
    {
        public int TotalTasks { get; } = totalTasks;
        public bool IsOptimizationJob { get; } = isOptimizationJob;
        public string? GroupRunKey { get; } = groupRunKey;
        public List<string> ChildStatuses { get; } = new(totalTasks);
        /// <summary>Mutable field — incremented via <see cref="Interlocked.Increment(ref int)"/>.</summary>
        public int CompletedCount;
    }
}
