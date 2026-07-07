namespace AlgoTradeForge.HistoryLoader.Application;

public sealed class HistoryLoaderOptions
{
    public static string DefaultDataRoot { get; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AlgoTradeForge",
            "History");

    public string DataRoot { get; init; } = DefaultDataRoot;
    public int MaxBackfillConcurrency { get; init; } = 3;
    public int CircuitBreakerCooldownMinutes { get; init; } = 15;
    public int NetworkFailureThreshold { get; init; } = 3;
    public int NetworkProbeIntervalSeconds { get; init; } = 60;
    public BinanceOptions Binance { get; init; } = new();
    public List<AssetCollectionConfig> Assets { get; init; } = [];
    public Dictionary<string, CollectionSchedule> Schedules { get; init; } = [];
    public AggregatorOptions Aggregator { get; init; } = new();
    public LoadOptions Load { get; init; } = new();
}

public sealed class LoadOptions
{
    public int MaxQueueDepth { get; init; } = 16;
    public int JobRetentionMinutes { get; init; } = 30;
    public int MaxMonthsPerRequest { get; init; } = 600;
    // Single-symbol cap: aggTrades zips are GB-scale; multi-symbol batching would multiply this if ever added.
    public int MaxTickMonthsPerRequest { get; init; } = 24;
}

/// <summary>Alt-bar aggregation knobs.</summary>
public sealed class AggregatorOptions
{
    /// <summary>Soft per-partition byte budget. Past this size the writer rolls into <c>&lt;YYYY&gt;-&lt;MM&gt;.p&lt;NN&gt;.csv</c>.</summary>
    public int MaxPartitionSizeMB { get; init; } = 100;

    /// <summary>Max parallel time-bar aggregations.</summary>
    public int MaxConcurrentJobs { get; init; } = 2;

    /// <summary>Max parallel tick-source aggregations (separate gate so I/O-heavy tick jobs don't block CPU-bound time-bar jobs).</summary>
    public int MaxConcurrentTickJobs { get; init; } = 1;

    /// <summary>Bounded job-queue capacity.</summary>
    public int MaxQueueDepth { get; init; } = 64;

    /// <summary>Terminal-state job retention before eviction (minutes).</summary>
    public int JobRetentionMinutes { get; init; } = 15;
}

public sealed class BinanceOptions
{
    public string SpotBaseUrl { get; init; } = "https://api.binance.com";
    public string FuturesBaseUrl { get; init; } = "https://fapi.binance.com";
    public string FuturesWsBaseUrl { get; init; } = "wss://fstream.binance.com";
    public string SpotWsBaseUrl { get; init; } = "wss://stream.binance.com:9443";
    public int MaxWeightPerMinute { get; init; } = 2400;
    public int WeightBudgetPercent { get; init; } = 40;
    public int RequestDelayMs { get; init; } = 50;
    public string ArchiveBaseUrl { get; init; } = "https://data.binance.vision";
    public int ArchiveDownloadConcurrency { get; init; } = 4;
}

public sealed class AssetCollectionConfig
{
    public required string Symbol { get; init; }
    public string Exchange { get; init; } = "binance";
    public required string Type { get; init; }
    public int DecimalDigits { get; init; } = 2;
    public DateOnly HistoryStart { get; init; } = new(2020, 1, 1);
    public List<FeedCollectionConfig> Feeds { get; init; } = [];
}

public sealed class CollectionSchedule
{
    /// <summary>Standard 5-field cron expression (e.g., "30 16 * * 1-5").</summary>
    public required string Cron { get; init; }

    /// <summary>IANA or Windows timezone ID. Defaults to UTC.</summary>
    public string TimeZone { get; init; } = "UTC";
}

public sealed class FeedCollectionConfig
{
    public required string Name { get; init; }
    public string Interval { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public DateOnly? HistoryStart { get; init; }
    public double GapThresholdMultiplier { get; init; } = 2.0;

    /// <summary>Opts a replenishable feed back into scheduled/stream collection (spec §1).</summary>
    public bool Eager { get; init; }
}
