namespace AlgoTradeForge.HistoryLoader.Application.Archive.Jobs;

public sealed class LoadJobRecord
{
    // Used by the registry to locate the per-key lock and clean indexes on terminal transition.
    // Always set by LoadJobRegistry.TryEnqueue — never left at default.
    internal string FeedKey { get; init; } = "";

    private readonly object _lock = new();

    public required LoadJob Job { get; init; }
    public required DateTimeOffset QueuedAt { get; init; }
    public LoadJobState State { get; set; } = LoadJobState.Queued;
    public int MonthsDone { get; set; }
    public int MonthsTotal { get; set; }
    public string? CurrentMonth { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    // Sets the progress triple atomically so a concurrent Snapshot() never observes
    // MonthsDone from one report paired with MonthsTotal/CurrentMonth from another.
    internal void SetProgress(int monthsDone, int monthsTotal, string currentMonth)
    {
        lock (_lock)
        {
            MonthsDone = monthsDone;
            MonthsTotal = monthsTotal;
            CurrentMonth = currentMonth;
        }
    }

    // Sets CompletedAt + error fields + State atomically so concurrent Snapshot() calls
    // never observe (State==Error && ErrorCode==null) or (State==Complete && CompletedAt==null).
    internal void MarkTerminal(LoadJobState state, DateTimeOffset completedAt, string? code, string? message)
    {
        lock (_lock)
        {
            CompletedAt = completedAt;
            ErrorCode = code;
            ErrorMessage = message;
            State = state;  // visibility anchor — set last
        }
    }

    public LoadJobSnapshot Snapshot()
    {
        lock (_lock)
        {
            return new LoadJobSnapshot(
                JobId: Job.JobId,
                State: State,
                QueuedAt: QueuedAt,
                CompletedAt: CompletedAt,
                MonthsDone: MonthsDone,
                MonthsTotal: MonthsTotal,
                CurrentMonth: CurrentMonth,
                ErrorCode: ErrorCode,
                ErrorMessage: ErrorMessage,
                Symbol: Job.Symbol,
                FeedName: Job.FeedName,
                Interval: Job.Interval,
                From: Job.From,
                To: Job.To);
        }
    }
}
