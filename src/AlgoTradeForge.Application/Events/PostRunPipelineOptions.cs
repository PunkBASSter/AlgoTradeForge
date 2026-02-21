namespace AlgoTradeForge.Application.Events;

public sealed record PostRunPipelineOptions
{
    public bool BuildDebugIndex { get; init; } = true;
    public string? TradeDbPath { get; init; }  // null → defaults to Data/trades.sqlite
}
