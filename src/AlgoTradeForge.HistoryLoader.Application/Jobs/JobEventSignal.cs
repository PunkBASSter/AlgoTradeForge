using System.Collections.Concurrent;

namespace AlgoTradeForge.HistoryLoader.Application.Jobs;

public sealed class JobEventSignal : IJobEventSignal
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _cells = new();

    // A reader creates the cell so a Signal that arrives before it still completes THIS task.
    public Task Next(string jobId) =>
        _cells.GetOrAdd(jobId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

    // §S2: no-op when no reader has registered — do NOT GetOrAdd here, or a completed TCS is parked
    // and the next Next() returns an already-complete task → the SSE tail loop busy-spins between
    // events. Only swap+complete an EXISTING cell; the reader's own drain picks up the durable tail.
    public void Signal(string jobId)
    {
        if (!_cells.TryGetValue(jobId, out var prev)) return;
        var fresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _cells.TryUpdate(jobId, fresh, prev);
        prev.TrySetResult();
    }

    public void Evict(string jobId)
    {
        if (_cells.TryRemove(jobId, out var cell)) cell.TrySetResult();
    }
}
