using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Infrastructure.Binance;
using AlgoTradeForge.HistoryLoader.Infrastructure.RateLimiting;
using AlgoTradeForge.HistoryLoader.Infrastructure.State;
using AlgoTradeForge.HistoryLoader.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddHistoryLoaderInfrastructure(this IServiceCollection services)
    {
        // Rate limiting — global limiter shared by all sources
        services.AddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<HistoryLoaderOptions>>().Value;
            return new WeightedRateLimiter(opts.Binance.MaxWeightPerMinute, opts.Binance.WeightBudgetPercent);
        });

        // Futures source rate limiter
        var futuresLimiterKey = "futures-rate-limiter";
        services.AddKeyedSingleton(futuresLimiterKey, (sp, _) =>
        {
            var global = sp.GetRequiredService<WeightedRateLimiter>();
            return new SourceRateLimiter(global);
        });

        // Concrete client singletons
        services.AddHttpClient<BinanceFuturesClient>();
        services.AddSingleton(sp =>
        {
            var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
            var httpClient = httpFactory.CreateClient(nameof(BinanceFuturesClient));
            var opts = sp.GetRequiredService<IOptions<HistoryLoaderOptions>>().Value;
            var rateLimiter = sp.GetRequiredKeyedService<SourceRateLimiter>(futuresLimiterKey);
            return new BinanceFuturesClient(httpClient, opts.Binance, rateLimiter);
        });

        services.AddHttpClient<BinanceSpotClient>();
        services.AddSingleton(sp =>
        {
            var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
            var httpClient = httpFactory.CreateClient(nameof(BinanceSpotClient));
            var opts = sp.GetRequiredService<IOptions<HistoryLoaderOptions>>().Value;
            var global = sp.GetRequiredService<WeightedRateLimiter>();
            var spotLimiter = new SourceRateLimiter(global);
            return new BinanceSpotClient(httpClient, opts.Binance, spotLimiter);
        });

        // Keyed DI — futures candles
        var futuresKey = "binance-futures";
        services.AddKeyedSingleton<ICandleFetcher>(futuresKey,
            (sp, _) => sp.GetRequiredService<BinanceFuturesClient>());

        // Keyed DI — futures feed fetchers (compound key: "{exchange}:{feedName}")
        services.AddKeyedSingleton<IFeedFetcher>($"{futuresKey}:{FeedNames.FundingRate}",
            (sp, _) => new DelegatingFeedFetcher(
                (symbol, _, fromMs, toMs, ct) =>
                    sp.GetRequiredService<BinanceFuturesClient>().FetchFundingRatesAsync(symbol, fromMs, toMs, ct)));

        services.AddKeyedSingleton<IFeedFetcher>($"{futuresKey}:{FeedNames.MarkPrice}",
            (sp, _) => new DelegatingFeedFetcher(
                (symbol, interval, fromMs, toMs, ct) =>
                    sp.GetRequiredService<BinanceFuturesClient>().FetchMarkPriceFeedAsync(symbol, interval!, fromMs, toMs, ct)));

        services.AddKeyedSingleton<IFeedFetcher>($"{futuresKey}:{FeedNames.OpenInterest}",
            (sp, _) => new DelegatingFeedFetcher(
                (symbol, interval, fromMs, toMs, ct) =>
                    sp.GetRequiredService<BinanceFuturesClient>().FetchOpenInterestAsync(symbol, interval!, fromMs, toMs, ct)));

        services.AddKeyedSingleton<IFeedFetcher>($"{futuresKey}:{FeedNames.LsRatioGlobal}",
            (sp, _) => new DelegatingFeedFetcher(
                (symbol, interval, fromMs, toMs, ct) =>
                    sp.GetRequiredService<BinanceFuturesClient>().FetchGlobalLongShortRatioAsync(symbol, interval!, fromMs, toMs, ct)));

        services.AddKeyedSingleton<IFeedFetcher>($"{futuresKey}:{FeedNames.LsRatioTopAccounts}",
            (sp, _) => new DelegatingFeedFetcher(
                (symbol, interval, fromMs, toMs, ct) =>
                    sp.GetRequiredService<BinanceFuturesClient>().FetchTopAccountRatioAsync(symbol, interval!, fromMs, toMs, ct)));

        services.AddKeyedSingleton<IFeedFetcher>($"{futuresKey}:{FeedNames.LsRatioTopPositions}",
            (sp, _) => new DelegatingFeedFetcher(
                (symbol, interval, fromMs, toMs, ct) =>
                    sp.GetRequiredService<BinanceFuturesClient>().FetchTopPositionRatioAsync(symbol, interval!, fromMs, toMs, ct)));

        services.AddKeyedSingleton<IFeedFetcher>($"{futuresKey}:{FeedNames.TakerVolume}",
            (sp, _) => new DelegatingFeedFetcher(
                (symbol, interval, fromMs, toMs, ct) =>
                    sp.GetRequiredService<BinanceFuturesClient>().FetchTakerVolumeAsync(symbol, interval!, fromMs, toMs, ct)));

        services.AddKeyedSingleton<IFeedFetcher>($"{futuresKey}:{FeedNames.Liquidations}",
            (sp, _) => new DelegatingFeedFetcher(
                (symbol, _, fromMs, toMs, ct) =>
                    sp.GetRequiredService<BinanceFuturesClient>().FetchLiquidationsAsync(symbol, fromMs, toMs, ct)));

        // Keyed DI — spot
        services.AddKeyedSingleton<ICandleFetcher>("binance-spot",
            (sp, _) => sp.GetRequiredService<BinanceSpotClient>());

        // Factory abstractions (replace direct IServiceProvider usage in Application layer)
        services.AddSingleton<IFeedFetcherFactory, FeedFetcherFactory>();
        services.AddSingleton<ICandleFetcherFactory, CandleFetcherFactory>();

        // Storage writers (share a WriteLockManager so scheduled + backfill don't collide)
        services.AddSingleton<WriteLockManager>();
        services.AddSingleton<ICandleWriter, CandleCsvWriter>();
        services.AddSingleton<IFeedWriter, FeedCsvWriter>();
        services.AddSingleton<ISchemaManager, FeedSchemaManager>();
        services.AddSingleton<IFeedStatusStore, FeedStatusManager>();

        return services;
    }
}
