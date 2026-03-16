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
    public BinanceOptions Binance { get; init; } = new();
    public List<AssetCollectionConfig> Assets { get; init; } = [];
}

public sealed class BinanceOptions
{
    public string SpotBaseUrl { get; init; } = "https://api.binance.com";
    public string FuturesBaseUrl { get; init; } = "https://fapi.binance.com";
    public string FuturesWsBaseUrl { get; init; } = "wss://fstream.binance.com";
    public int MaxWeightPerMinute { get; init; } = 2400;
    public int WeightBudgetPercent { get; init; } = 40;
    public int RequestDelayMs { get; init; } = 50;
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

public sealed class FeedCollectionConfig
{
    public required string Name { get; init; }
    public string Interval { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public DateOnly? HistoryStart { get; init; }
    public double GapThresholdMultiplier { get; init; } = 2.0;
}
