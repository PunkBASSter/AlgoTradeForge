namespace AlgoTradeForge.HistoryLoader.Application.Jobs;

// Per-kind dispatch doorbell. One keyed instance per gated kind ("load", "materialize", ...).
// Carries job ids only — the durable store owns the job state; a wakeup is a hint to go look.
public interface IJobWakeupQueue
{
    // False when the bounded channel is full — the caller must roll back its just-created job row.
    bool TryEnqueue(string jobId);

    IAsyncEnumerable<string> Reader(CancellationToken ct);

    // On worker start, re-arm the queue from rows the store still marks 'queued' after a restart.
    int SeedFromQueued(IEnumerable<string> jobIds);
}
