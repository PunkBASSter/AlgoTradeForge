using AlgoTradeForge.Infrastructure.Live.Binance;
using Microsoft.Extensions.Configuration;

namespace AlgoTradeForge.Infrastructure.Tests.Live.Testnet;

public static class BinanceTestnetCredentials
{
    private static readonly Lazy<(string? Key, string? Secret)> _credentials = new(LoadCredentials);

    public static string? ApiKey => _credentials.Value.Key;
    public static string? ApiSecret => _credentials.Value.Secret;

    public static bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(ApiSecret);

    public const string SkipReason =
        "Binance testnet credentials not configured. " +
        "Set via 'dotnet user-secrets set BinanceLive:Accounts:paper:ApiKey <key>' " +
        "or env vars BINANCE_TESTNET_APIKEY / BINANCE_TESTNET_APISECRET.";

    public static BinanceAccountConfig CreateAccountConfig() => new()
    {
        RestUrl = "https://testnet.binance.vision",
        MarketStreamUrl = "wss://stream.testnet.binance.vision",
        WebSocketApiUrl = "wss://ws-api.testnet.binance.vision/ws-api/v3",
        ApiKey = ApiKey ?? string.Empty,
        ApiSecret = ApiSecret ?? string.Empty,
    };

    private static (string? Key, string? Secret) LoadCredentials()
    {
        // 1. Try .NET User Secrets (shared with WebApi project)
        try
        {
            var config = new ConfigurationBuilder()
                .AddUserSecrets("9c3c6654-bd16-4993-bf63-c3d2e77092b3")
                .Build();

            var key = config["BinanceLive:Accounts:paper:ApiKey"];
            var secret = config["BinanceLive:Accounts:paper:ApiSecret"];

            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(secret))
                return (key, secret);
        }
        catch
        {
            // User secrets not available — fall through to env vars
        }

        // 2. Fallback to environment variables
        return (
            Environment.GetEnvironmentVariable("BINANCE_TESTNET_APIKEY"),
            Environment.GetEnvironmentVariable("BINANCE_TESTNET_APISECRET"));
    }
}
