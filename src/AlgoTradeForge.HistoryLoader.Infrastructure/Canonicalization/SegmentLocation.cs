namespace AlgoTradeForge.HistoryLoader.Infrastructure.Canonicalization;

public readonly record struct SegmentLocation(
    string Venue,
    string InstrumentOrVenue,
    string StreamName,
    long CreatedAtMs,
    long FirstSequence,
    string Key);
