namespace AlgoTradeForge.HistoryLoader.Application.Index;

public sealed class IndexOptions
{
    /// <summary>Null resolves to %LOCALAPPDATA%/AlgoTradeForge/history-index.sqlite.</summary>
    public string? Path { get; init; }

    public int DriftSweepMinutes { get; init; } = 60;
}
