namespace AlgoTradeForge.Application.Live;

public sealed class BinanceLiveOptions
{
    // Production endpoints
    public string BaseRestUrl { get; init; } = "https://api.binance.com";
    public string BaseWsUrl { get; init; } = "wss://stream.binance.com:9443";
    public string ApiKey { get; init; } = "";
    public string ApiSecret { get; init; } = "";

    // Testnet endpoints (used for paper trading — real exchange, virtual money)
    public string TestnetRestUrl { get; init; } = "https://testnet.binance.vision";
    public string TestnetWsUrl { get; init; } = "wss://testnet.binance.vision";
    public string TestnetApiKey { get; init; } = "";
    public string TestnetApiSecret { get; init; } = "";

    public TimeSpan ListenKeyRefreshInterval { get; init; } = TimeSpan.FromMinutes(30);
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(5);
    public int MaxReconnectAttempts { get; init; } = 10;

    /// <summary>
    /// Returns effective REST URL based on trading mode.
    /// </summary>
    public string GetRestUrl(bool paperTrading) => paperTrading ? TestnetRestUrl : BaseRestUrl;

    /// <summary>
    /// Returns effective WebSocket URL based on trading mode.
    /// Paper trading uses production WS for market data (testnet streams are unreliable),
    /// but testnet WS for user data stream.
    /// </summary>
    public string GetMarketDataWsUrl(bool paperTrading) => paperTrading ? BaseWsUrl : BaseWsUrl;

    /// <summary>
    /// Returns effective user data stream WS URL based on trading mode.
    /// </summary>
    public string GetUserDataWsUrl(bool paperTrading) => paperTrading ? TestnetWsUrl : BaseWsUrl;

    public string GetApiKey(bool paperTrading) => paperTrading ? TestnetApiKey : ApiKey;
    public string GetApiSecret(bool paperTrading) => paperTrading ? TestnetApiSecret : ApiSecret;
}
