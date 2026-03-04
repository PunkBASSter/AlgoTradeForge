namespace AlgoTradeForge.Application.Live;

// TODO: try to extract binance details to infra layer
public sealed class BinanceLiveOptions
{
    public Dictionary<string, BinanceAccountConfig> Accounts { get; init; } = new();
    public TimeSpan ListenKeyRefreshInterval { get; init; } = TimeSpan.FromMinutes(30);
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(5);
    public int MaxReconnectAttempts { get; init; } = 10;
}

public sealed class BinanceAccountConfig
{
    public required string RestUrl { get; init; }
    public required string WsUrl { get; init; }
    public required string ApiKey { get; init; }
    public required string ApiSecret { get; init; }
}
