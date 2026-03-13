using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
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
            var opts = sp.GetRequiredService<IOptions<HistoryLoaderOptions>>().Value;
            var global = sp.GetRequiredService<WeightedRateLimiter>();
            return new SourceRateLimiter(global, opts.Binance.FuturesBaseUrl);
        });

        // Binance Futures API client → IFuturesDataFetcher
        services.AddHttpClient<BinanceFuturesClient>();
        services.AddSingleton<IFuturesDataFetcher>(sp =>
        {
            var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
            var httpClient = httpFactory.CreateClient(nameof(BinanceFuturesClient));
            var opts = sp.GetRequiredService<IOptions<HistoryLoaderOptions>>().Value;
            var rateLimiter = sp.GetRequiredKeyedService<SourceRateLimiter>(futuresLimiterKey);
            return new BinanceFuturesClient(httpClient, opts.Binance, rateLimiter);
        });

        // Binance Spot API client → ISpotDataFetcher
        services.AddHttpClient<BinanceSpotClient>();
        services.AddSingleton<ISpotDataFetcher>(sp =>
        {
            var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
            var httpClient = httpFactory.CreateClient(nameof(BinanceSpotClient));
            var opts = sp.GetRequiredService<IOptions<HistoryLoaderOptions>>().Value;
            var global = sp.GetRequiredService<WeightedRateLimiter>();
            var spotLimiter = new SourceRateLimiter(global, opts.Binance.SpotBaseUrl);
            return new BinanceSpotClient(httpClient, opts.Binance, spotLimiter);
        });

        // Storage writers
        services.AddSingleton<ICandleWriter, CandleCsvWriter>();
        services.AddSingleton<IFeedWriter, FeedCsvWriter>();
        services.AddSingleton<ISchemaManager, FeedSchemaManager>();
        services.AddSingleton<IFeedStatusStore, FeedStatusManager>();

        return services;
    }
}
