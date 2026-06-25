namespace AlgoTradeForge.LiveHost.Application.Live.Recovery;

/// <summary>
/// Attempts to make a detected gap available in the archive within <paramref name="policy"/>'s
/// budget. Returns true iff the archive now covers the gap (replay can re-read it contiguously).
/// Venue-specific: Binance issues REST backfill (generous budget); IB returns false fast (budget 0).
/// </summary>
public interface IBackfillRequester
{
    Task<bool> TryBackfill(ReplayRequest context, Discontinuity gap, RecoveryPolicy policy, CancellationToken ct = default);
}
