namespace AlgoTradeForge.HistoryLoader.Application.Archive.Jobs;

public abstract record LoadEnqueueOutcome
{
    public sealed record Accepted(LoadJobRecord Record) : LoadEnqueueOutcome;
    public sealed record FeedBusy(string ActiveJobId) : LoadEnqueueOutcome;
    public sealed record QueueFull : LoadEnqueueOutcome;
}
