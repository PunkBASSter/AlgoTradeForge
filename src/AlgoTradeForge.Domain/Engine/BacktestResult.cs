using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Engine;

/// <summary>
/// A single equity snapshot pairing a bar timestamp with the portfolio value at that point.
/// Domain-layer Int64 convention: Value is in scaled long units.
/// </summary>
public readonly record struct EquitySnapshot(long TimestampMs, long Value);

public sealed record BacktestResult(
    Portfolio FinalPortfolio,
    IReadOnlyList<Fill> Fills,
    IReadOnlyList<EquitySnapshot> EquityCurve,
    int TotalBarsProcessed,
    TimeSpan Duration);
