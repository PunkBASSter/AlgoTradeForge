namespace AlgoTradeForge.Domain.Reporting;

/// <param name="PriceMoveTicks">Per-unit price move of the round trip in ticks
/// (int64 prices are tick-scaled, so this is exit − entry, signed by direction).
/// Quantity- and sizing-independent, comparable across assets.</param>
public readonly record struct ClosedTrade(long ExitTimestampMs, long RealizedPnl, long PriceMoveTicks);
