using AlgoTradeForge.Storage;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Aggregation;
using AlgoTradeForge.HistoryLoader.Application.Archive;
using AlgoTradeForge.HistoryLoader.Application.Archive.Jobs;
using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Infrastructure.Archive;
using AlgoTradeForge.HistoryLoader.Infrastructure.Binance;
using AlgoTradeForge.HistoryLoader.Infrastructure.Index;
using AlgoTradeForge.HistoryLoader.Infrastructure.RateLimiting;
using AlgoTradeForge.HistoryLoader.Infrastructure.State;
using AlgoTradeForge.HistoryLoader.Infrastructure.Storage;
using AlgoTradeForge.HistoryLoader.Infrastructure.Storage.Buffered;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

        // Funding-info fetcher — single-shot endpoint, no per-symbol/per-feed routing.
        services.AddSingleton<IFundingInfoFetcher>(
            sp => sp.GetRequiredService<BinanceFuturesClient>());

        // Keyed DI — futures feed fetchers (compound key: "{exchange}:{feedName}")
        services.AddKeyedSingleton<IFeedFetcher>($"{futuresKey}:{FeedNames.FundingRate}",
            (sp, _) => new DelegatingFeedFetcher(
                (symbol, _, fromMs, toMs, ct) =>
                    sp.GetRequiredService<BinanceFuturesClient>().FetchFundingRatesAsync(symbol, fromMs, toMs, ct)));

        services.AddKeyedSingleton<IFeedFetcher>($"{futuresKey}:{FeedNames.MarkPrice}",
            (sp, _) => new DelegatingFeedFetcher(
                (symbol, interval, fromMs, toMs, ct) =>
                    sp.GetRequiredService<BinanceFuturesClient>().FetchMarkPriceFeedAsync(symbol, interval!, fromMs, toMs, ct)));

        services.AddKeyedSingleton<IFeedFetcher>($"{futuresKey}:{FeedNames.PremiumIndex}",
            (sp, _) => new DelegatingFeedFetcher(
                (symbol, interval, fromMs, toMs, ct) =>
                    sp.GetRequiredService<BinanceFuturesClient>().FetchPremiumIndexFeedAsync(symbol, interval!, fromMs, toMs, ct)));

        services.AddKeyedSingleton<IFeedFetcher>($"{futuresKey}:{FeedNames.IndexPrice}",
            (sp, _) => new DelegatingFeedFetcher(
                (symbol, interval, fromMs, toMs, ct) =>
                    sp.GetRequiredService<BinanceFuturesClient>().FetchIndexPriceFeedAsync(symbol, interval!, fromMs, toMs, ct)));

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

        services.AddKeyedSingleton<IFeedFetcher>($"{futuresKey}:{FeedNames.Liquidations}",
            (sp, _) => new DelegatingFeedFetcher(
                (symbol, _, fromMs, toMs, ct) =>
                    sp.GetRequiredService<BinanceFuturesClient>().FetchLiquidationsAsync(symbol, fromMs, toMs, ct)));

        services.AddKeyedSingleton<IFeedFetcher>($"{futuresKey}:{FeedNames.Ticks}",
            (sp, _) => new DelegatingFeedFetcher(
                (symbol, _, fromMs, toMs, ct) =>
                    sp.GetRequiredService<BinanceFuturesClient>().FetchAggTradesAsync(symbol, fromMs, toMs, ct)));

        // Keyed DI — spot
        var spotKey = "binance-spot";
        services.AddKeyedSingleton<ICandleFetcher>(spotKey,
            (sp, _) => sp.GetRequiredService<BinanceSpotClient>());

        services.AddKeyedSingleton<IFeedFetcher>($"{spotKey}:{FeedNames.Ticks}",
            (sp, _) => new DelegatingFeedFetcher(
                (symbol, _, fromMs, toMs, ct) =>
                    sp.GetRequiredService<BinanceSpotClient>().FetchAggTradesAsync(symbol, fromMs, toMs, ct)));

        // Factory abstractions (replace direct IServiceProvider usage in Application layer)
        services.AddSingleton<IFeedFetcherFactory, FeedFetcherFactory>();
        services.AddSingleton<ICandleFetcherFactory, CandleFetcherFactory>();

        // File storage backend selected from StorageOptions:Backend. HistoryLoader registers
        // these itself rather than calling AddInfrastructure, which pulls in SQLite repos and
        // live-trading wiring that this host has no use for. The factories live in the Storage
        // project so both hosts pick the same backend per the same config; StorageOptions
        // binding is the host's responsibility — see Program.cs (Storage section).
        services.AddSingleton<IFileStorage>(FileStorageFactory.Build);
        services.AddSingleton<IPartitionTailIndex>(FileStorageFactory.BuildTailIndex);

        // Storage writers (share a WriteLockManager so scheduled + backfill don't collide)
        services.AddSingleton<WriteLockManager>();
        services.AddSingleton<ICandleWriter, CandleCsvWriter>();
        services.AddSingleton<IFeedWriter, FeedCsvWriter>();
        services.AddSingleton<ITickFeedWriter, DailyTickCsvWriter>();
        services.AddSingleton<IBookTickerWriter, DailyBookTickerCsvWriter>();

        // IBufferedPartitionWriter aliases for the flush service. Each resolves to the same
        // singleton as its interface counterpart — no duplicate instances.
        services.AddSingleton<IBufferedPartitionWriter>(sp => (IBufferedPartitionWriter)sp.GetRequiredService<ICandleWriter>());
        services.AddSingleton<IBufferedPartitionWriter>(sp => (IBufferedPartitionWriter)sp.GetRequiredService<IFeedWriter>());
        services.AddSingleton<IBufferedPartitionWriter>(sp => (IBufferedPartitionWriter)sp.GetRequiredService<ITickFeedWriter>());
        services.AddSingleton<IBufferedPartitionWriter>(sp => (IBufferedPartitionWriter)sp.GetRequiredService<IBookTickerWriter>());

        services.AddHostedService<BufferedWriterFlushService>();

        services.AddSingleton<ISchemaManager, FeedSchemaManager>();

        services.AddSingleton<FeedStatusManager>();
        services.AddSingleton<IFeedStatusStore>(sp => new IndexingFeedStatusStore(
            sp.GetRequiredService<FeedStatusManager>(),
            sp.GetRequiredService<IIndexMaintenance>()));

        services.AddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<HistoryLoaderOptions>>().Value;
            return new HistoryIndexInitializer(HistoryIndexInitializer.ResolvePath(opts.Index));
        });
        services.AddSingleton<IHistoryIndex>(sp =>
            new SqliteHistoryIndex(sp.GetRequiredService<HistoryIndexInitializer>()));
        services.AddSingleton<IFeedMonthScanner, FeedMonthScanner>();
        services.AddSingleton<IndexMaintenanceQueue>();
        services.AddSingleton<IIndexMaintenance>(sp => sp.GetRequiredService<IndexMaintenanceQueue>());
        services.AddSingleton<IIndexRebuilder, IndexRebuilder>();
        services.AddSingleton<IndexWorkProcessor>();

        services.AddSingleton<IMonthCoverageCalculator, MonthCoverageCalculator>();

        services.AddSingleton<AggregatedDirSweeper>();

        // Instrument-meta — named HttpClient for Binance exchangeInfo (spot + futures)
        services.AddHttpClient("binance-meta");
        services.AddSingleton<IInstrumentMetaProvider, BinanceInstrumentMetaProvider>();

        // Archive backfill — named HttpClient for data.binance.vision downloads
        services.AddHttpClient("binance-archive", (sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<HistoryLoaderOptions>>().Value;
            client.BaseAddress = new Uri(opts.Binance.ArchiveBaseUrl);
            client.Timeout = TimeSpan.FromMinutes(5);
        });
        services.AddSingleton<IBinanceArchiveClient, BinanceArchiveClient>();
        services.AddSingleton<IPartitionFileWriter, PartitionFileWriter>();
        services.AddSingleton<ILoadAssetResolver, BinanceLoadAssetResolver>();
        services.AddSingleton<ILoadJobRegistry, LoadJobRegistry>();
        services.AddSingleton<ArchiveBackfillService>();
        services.AddSingleton<ArchiveMaterializerRegistry>();

        // Materializer set — spec §1 classification table
        services.AddSingleton<IArchiveMaterializer>(sp => new KlinesArchiveMaterializer(
            FeedNames.Candles, "klines", supportsSpot: true,
            sp.GetRequiredService<IBinanceArchiveClient>(),
            sp.GetRequiredService<IPartitionFileWriter>(),
            sp.GetRequiredService<ISchemaManager>(),
            sp.GetRequiredService<IFeedStatusStore>(),
            sp.GetRequiredService<ILogger<KlinesArchiveMaterializer>>()));
        services.AddSingleton<IArchiveMaterializer>(sp => new KlinesArchiveMaterializer(
            FeedNames.MarkPrice, "markPriceKlines", supportsSpot: false,
            sp.GetRequiredService<IBinanceArchiveClient>(),
            sp.GetRequiredService<IPartitionFileWriter>(),
            sp.GetRequiredService<ISchemaManager>(),
            sp.GetRequiredService<IFeedStatusStore>(),
            sp.GetRequiredService<ILogger<KlinesArchiveMaterializer>>()));
        services.AddSingleton<IArchiveMaterializer>(sp => new MetricsArchiveMaterializer(
            FeedNames.OpenInterest,
            sp.GetRequiredService<IBinanceArchiveClient>(),
            sp.GetRequiredService<IPartitionFileWriter>(),
            sp.GetRequiredService<ISchemaManager>(),
            sp.GetRequiredService<IFeedStatusStore>(),
            sp.GetRequiredService<ILogger<MetricsArchiveMaterializer>>()));
        services.AddSingleton<IArchiveMaterializer>(sp => new MetricsArchiveMaterializer(
            FeedNames.LsRatioGlobal,
            sp.GetRequiredService<IBinanceArchiveClient>(),
            sp.GetRequiredService<IPartitionFileWriter>(),
            sp.GetRequiredService<ISchemaManager>(),
            sp.GetRequiredService<IFeedStatusStore>(),
            sp.GetRequiredService<ILogger<MetricsArchiveMaterializer>>()));
        services.AddSingleton<IArchiveMaterializer>(sp => new MetricsArchiveMaterializer(
            FeedNames.LsRatioTopAccounts,
            sp.GetRequiredService<IBinanceArchiveClient>(),
            sp.GetRequiredService<IPartitionFileWriter>(),
            sp.GetRequiredService<ISchemaManager>(),
            sp.GetRequiredService<IFeedStatusStore>(),
            sp.GetRequiredService<ILogger<MetricsArchiveMaterializer>>()));
        services.AddSingleton<IArchiveMaterializer>(sp => new MetricsArchiveMaterializer(
            FeedNames.LsRatioTopPositions,
            sp.GetRequiredService<IBinanceArchiveClient>(),
            sp.GetRequiredService<IPartitionFileWriter>(),
            sp.GetRequiredService<ISchemaManager>(),
            sp.GetRequiredService<IFeedStatusStore>(),
            sp.GetRequiredService<ILogger<MetricsArchiveMaterializer>>()));
        services.AddSingleton<IArchiveMaterializer>(sp => new AggTradesArchiveMaterializer(
            sp.GetRequiredService<IBinanceArchiveClient>(),
            sp.GetRequiredService<IPartitionFileWriter>(),
            sp.GetRequiredService<ISchemaManager>(),
            sp.GetRequiredService<IFeedStatusStore>(),
            sp.GetRequiredService<ILogger<AggTradesArchiveMaterializer>>()));
        services.AddSingleton<IArchiveMaterializer>(sp => new FundingRateArchiveMaterializer(
            sp.GetRequiredService<IBinanceArchiveClient>(),
            sp.GetRequiredService<IPartitionFileWriter>(),
            sp.GetRequiredService<ISchemaManager>(),
            sp.GetRequiredService<IFeedStatusStore>(),
            sp.GetRequiredService<ILogger<FundingRateArchiveMaterializer>>()));
        services.AddSingleton<IArchiveMaterializer>(sp => new TakerVolumeArchiveMaterializer(
            sp.GetRequiredService<IBinanceArchiveClient>(),
            sp.GetRequiredService<IPartitionFileWriter>(),
            sp.GetRequiredService<ISchemaManager>(),
            sp.GetRequiredService<IFeedStatusStore>(),
            sp.GetRequiredService<ILogger<TakerVolumeArchiveMaterializer>>()));

        return services;
    }
}
