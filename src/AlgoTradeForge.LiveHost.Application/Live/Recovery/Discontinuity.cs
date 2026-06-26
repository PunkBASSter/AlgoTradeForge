namespace AlgoTradeForge.LiveHost.Application.Live.Recovery;

/// <summary>
/// A break in the source-record stream the catch-up could not bridge, as a time window. Venue-
/// agnostic: every venue has timestamps, and every consumer (backfill REST start/end, HistoryLoader
/// heal, FE marker) works on time ranges. The venue-specific detection signal (Binance aggId
/// discontinuity, IB connection events) stays inside the detector and never reaches this marker.
/// </summary>
public readonly record struct Discontinuity(long FromTs, long ToTs, DiscontinuityReason Reason);
