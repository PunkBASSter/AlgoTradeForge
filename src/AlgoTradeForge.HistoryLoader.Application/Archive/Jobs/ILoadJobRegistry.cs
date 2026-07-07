namespace AlgoTradeForge.HistoryLoader.Application.Archive.Jobs;

public interface ILoadJobRegistry
{
    // FeedKey = $"{assetDir}|{feedName}|{interval}". One active job per feed key.
    LoadEnqueueOutcome TryEnqueue(LoadJob job, string feedKey);

    // Terminal records past JobRetentionMinutes are lazily evicted here.
    LoadJobSnapshot? Get(string jobId);

    // Returns the active (queued/running) job id for the given assetDir, or null.
    string? ActiveJobForSymbol(string assetDir);

    // Blocking channel read. Returns null when the token is cancelled or the channel closes.
    Task<LoadJob?> Dequeue(CancellationToken ct);

    void OnStarted(string jobId);
    void OnProgress(string jobId, int monthsDone, int monthsTotal, string currentMonth);
    void OnCompleted(string jobId);
    void OnErrored(string jobId, string code, string message);
}
