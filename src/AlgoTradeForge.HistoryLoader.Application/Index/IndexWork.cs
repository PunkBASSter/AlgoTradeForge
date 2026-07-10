namespace AlgoTradeForge.HistoryLoader.Application.Index;

public abstract record IndexWork
{
    public sealed record FeedTouched(string AssetDir, string FeedName, string Interval) : IndexWork;
    public sealed record ManifestTouched(string AssetDir) : IndexWork;
    public sealed record Rebuild(string JobId) : IndexWork;
}
