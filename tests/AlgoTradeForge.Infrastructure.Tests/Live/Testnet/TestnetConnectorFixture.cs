using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Engine;
using Xunit;
using AlgoTradeForge.Domain.Live;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Infrastructure.Live.Binance;
using AlgoTradeForge.Infrastructure.Tests.TestUtilities;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlgoTradeForge.Infrastructure.Tests.Live.Testnet;

public sealed class TestnetConnectorFixture : IAsyncLifetime
{
    public BinanceLiveConnector? Connector { get; private set; }
    public TestnetOrderStrategy? Strategy { get; private set; }
    public Asset? Asset { get; private set; }
    public Guid SessionId { get; private set; }

    /// <summary>Current BTCUSDT price (Int64 scaled by TickSize) fetched via REST at startup.</summary>
    public long LastPrice { get; private set; }

    public async ValueTask InitializeAsync()
    {
        if (!BinanceTestnetCredentials.IsConfigured)
            return;

        var accountConfig = BinanceTestnetCredentials.CreateAccountConfig();
        var sharedOptions = new BinanceLiveOptions();
        var validator = new OrderValidator();
        var logger = NullLogger<BinanceLiveConnector>.Instance;

        Connector = new BinanceLiveConnector("testnet", accountConfig, sharedOptions, validator, logger);
        await Connector.ConnectAsync();

        Asset = CryptoAsset.Create("BTCUSDT", "Binance", decimalDigits: 2,
            minOrderQuantity: 0.00010m, maxOrderQuantity: 9000m, quantityStepSize: 0.00010m);

        // Fetch current price via the connector's API client — instant, no bar wait needed
        var tickerPrice = await Connector.GetTickerPriceAsync("BTCUSDT");
        LastPrice = (long)(tickerPrice / Asset.TickSize);

        Strategy = new TestnetOrderStrategy(new TestnetOrderStrategyParams());
        SessionId = Guid.NewGuid();

        var initialCash = (long)(100m / Asset.TickSize); // 100 USDT scaled

        var config = new LiveSessionConfig
        {
            SessionId = SessionId,
            Strategy = Strategy,
            Subscriptions = [new DataSubscription(Asset, TimeSpan.FromMinutes(1))],
            PrimaryAsset = Asset,
            InitialCash = initialCash,
            Routing = LiveEventRouting.All,
            AccountName = "testnet",
        };

        await Connector.AddSessionAsync(config);
    }

    public async ValueTask DisposeAsync()
    {
        if (Connector is null) return;

        try
        {
            if (SessionId != Guid.Empty)
                await Connector.RemoveSessionAsync(SessionId);
        }
        catch { /* best-effort cleanup */ }

        await Connector.StopAsync();
    }
}

[CollectionDefinition("BinanceTestnet")]
public sealed class BinanceTestnetCollection : ICollectionFixture<TestnetConnectorFixture>;
