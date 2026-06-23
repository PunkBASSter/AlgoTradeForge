using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Engine;
using Xunit;
using AlgoTradeForge.Domain.Live;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Subscriptions;
using AlgoTradeForge.LiveHost.Infrastructure.Live.Binance;
using AlgoTradeForge.LiveHost.Infrastructure.Live.DataPlane;
using AlgoTradeForge.LiveHost.Infrastructure.Tests.TestUtilities;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.Testnet;

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

        var klineWs = new BinanceWebSocketManager(
            accountConfig.MarketStreamUrl, sharedOptions.ReconnectDelay,
            sharedOptions.MaxReconnectAttempts, NullLogger.Instance);
        var resolver = new BarSourceResolver(klineWs);
        var dispatch = new StrategyDispatch(NullLogger<StrategyDispatch>.Instance);
        var router = new TickRouter(resolver, dispatch, NullLogger<TickRouter>.Instance);

        Connector = new BinanceLiveConnector(
            "testnet", accountConfig, sharedOptions, validator, router, dispatch, logger);
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
            Subscriptions = [TestSubs.Of(Asset, new TimeFrame(TimeSpan.FromMinutes(1)))],
            InitialCash = initialCash,
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
