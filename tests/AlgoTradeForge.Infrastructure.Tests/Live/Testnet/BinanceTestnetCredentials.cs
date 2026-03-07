using AlgoTradeForge.Infrastructure.Live.Binance;

namespace AlgoTradeForge.Infrastructure.Tests.Live.Testnet;

public static class BinanceTestnetCredentials
{
    public static string? ApiKey { get; } = Environment.GetEnvironmentVariable("BINANCE_TESTNET_APIKEY");
    public static string? ApiSecret { get; } = Environment.GetEnvironmentVariable("BINANCE_TESTNET_APISECRET");

    public static bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(ApiSecret);

    public const string SkipReason =
        "Binance testnet credentials not configured. Set BINANCE_TESTNET_APIKEY and BINANCE_TESTNET_APISECRET environment variables.";

    public static BinanceAccountConfig CreateAccountConfig() => new()
    {
        RestUrl = "https://testnet.binance.vision",
        MarketStreamUrl = "wss://stream.testnet.binance.vision",
        WebSocketApiUrl = "wss://ws-api.testnet.binance.vision/ws-api/v3",
        ApiKey = ApiKey ?? string.Empty,
        ApiSecret = ApiSecret ?? string.Empty,
    };
}
