using AlgoTradeForge.Application.CandleIngestion;

namespace AlgoTradeForge.CandleIngestor;

public sealed record CandleIngestorOptions
{
    public string DataRoot { get; init; } = CandleStorageOptions.DefaultDataRoot;
    public int ScheduleIntervalHours { get; init; } = 6;
    public bool RunOnStartup { get; init; } = true;
    public Dictionary<string, AdapterOptions> Adapters { get; init; } = [];
    public List<IngestorAssetConfig> Assets { get; init; } = [];
}

public sealed record AdapterOptions
{
    public required string Type { get; init; }
    public required string BaseUrl { get; init; }
    public int RateLimitPerMinute { get; init; } = 1200;
    public int RequestDelayMs { get; init; } = 100;
}

public sealed record IngestorAssetConfig
{
    public required string Symbol { get; init; }
    public required string Exchange { get; init; }
    public TimeSpan SmallestInterval { get; init; } = TimeSpan.FromMinutes(1);
    public int DecimalDigits { get; init; } = 2;
    public DateOnly HistoryStart { get; init; } = new(2020, 1, 1);
}
