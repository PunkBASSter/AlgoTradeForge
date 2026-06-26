namespace AlgoTradeForge.LiveHost.Infrastructure.Live.Recovery;

// Placeholder: a real REST aggTrade backfill client is a follow-up (spec open point #2).
public sealed class NullAggTradeBackfillClient : IAggTradeBackfillClient
{
    public Task<bool> FetchAndArchive(string instrument, long fromTs, long toTs, CancellationToken ct)
        => Task.FromResult(false);
}
