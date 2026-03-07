namespace AlgoTradeForge.Infrastructure.Live.Binance;

public sealed class BinanceLiveOptions
{
    public Dictionary<string, BinanceAccountConfig> Accounts { get; init; } = new();
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(5);
    public int MaxReconnectAttempts { get; init; } = 10;
}

public sealed class BinanceAccountConfig
{
    public required string RestUrl { get; init; }
    public required string MarketStreamUrl { get; init; }
    public required string WebSocketApiUrl { get; init; }
    public required string ApiKey { get; init; }
    public required string ApiSecret { get; init; }
}
