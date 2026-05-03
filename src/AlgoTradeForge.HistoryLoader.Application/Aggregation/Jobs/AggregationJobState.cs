namespace AlgoTradeForge.HistoryLoader.Application.Aggregation.Jobs;

/// <summary>
/// Lifecycle states for an aggregation job (TRD §5.4 / §6.5). Terminal states are
/// <see cref="Complete"/>, <see cref="Error"/>, and <see cref="Cancelled"/>; all retain in
/// the registry for <c>JobRetentionMinutes</c> before SSE replay returns 410 Gone.
/// </summary>
public enum AggregationJobState
{
    Queued,
    Running,
    Complete,
    Error,
    Cancelled,
}

public static class AggregationJobStateExtensions
{
    public static bool IsTerminal(this AggregationJobState state) =>
        state is AggregationJobState.Complete
              or AggregationJobState.Error
              or AggregationJobState.Cancelled;

    public static bool IsActive(this AggregationJobState state) =>
        state is AggregationJobState.Queued or AggregationJobState.Running;
}
