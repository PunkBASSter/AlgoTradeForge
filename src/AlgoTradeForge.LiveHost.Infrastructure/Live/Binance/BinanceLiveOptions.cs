namespace AlgoTradeForge.LiveHost.Infrastructure.Live.Binance;

public sealed class BinanceLiveOptions
{
    public Dictionary<string, BinanceAccountConfig> Accounts { get; init; } = new();
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(5);
    public int MaxReconnectAttempts { get; init; } = 10;
    public TimeSpan ReconciliationInterval { get; init; } = TimeSpan.FromSeconds(30);
    public int IngestChannelCapacity { get; init; } = 4096;
    public int LiveChannelCapacity { get; init; } = 1024;
    public string MarketStreamUrl { get; init; } = "wss://stream.binance.com:9443";
    public Dictionary<string, TickScale> InstrumentScales { get; init; } = new();
}

public sealed class BinanceAccountConfig
{
    public required string RestUrl { get; init; }
    public required string MarketStreamUrl { get; init; }
    public required string WebSocketApiUrl { get; init; }
    public required string ApiKey { get; init; }
    public required string ApiSecret { get; init; }
}
