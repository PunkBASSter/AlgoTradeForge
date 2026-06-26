namespace AlgoTradeForge.LiveHost.Infrastructure.Live.Recovery;

/// <summary>Fetches aggTrades for [fromTs,toTs] from the venue REST API and writes them into the
/// archive the replay source reads. Returns true when the range is now covered.</summary>
public interface IAggTradeBackfillClient
{
    Task<bool> FetchAndArchive(string instrument, long fromTs, long toTs, CancellationToken ct);
}
