using AlgoTradeForge.Application.CandleIngestion;
using AlgoTradeForge.Live.Relay;
using AlgoTradeForge.LiveHost.Application.Live.DataPlane;
using AlgoTradeForge.LiveHost.Application.Live.Recovery;
using AlgoTradeForge.LiveHost.Application.Collection;
using AlgoTradeForge.LiveHost.Infrastructure.Live.Recovery;
using AlgoTradeForge.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

public static class IbDataPlaneServiceCollectionExtensions
{
    public static IServiceCollection AddIbDataPlane(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<IbDataPlaneOptions>(configuration.GetSection("Ib"));
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<IbDataPlaneOptions>>().Value);
        services.AddSingleton(sp =>
        {
            var o = sp.GetRequiredService<IbDataPlaneOptions>();
            return new IbConnectionOptions(o.Host, o.Port, o.ClientId);
        });

        services.AddSingleton<IbWrapper>();
        services.AddSingleton<IbConnection>();
        services.AddSingleton<IIbMarketDataClient, IbConnectionMarketDataClient>();

        // IbSession is IAsyncDisposable; DI container disposes it on shutdown.
        services.AddSingleton<IbSession>();
        services.AddSingleton<IIbMarketDataSession>(sp => sp.GetRequiredService<IbSession>());

        services.AddSingleton<IIbContractDetailsClient, IbConnectionContractDetailsClient>();
        services.AddSingleton<IIbContractResolver, IbContractResolver>();
        services.AddSingleton<IIbHistoricalTicksClient, IbConnectionHistoricalTicksClient>();

        services.AddSingleton<IIbInstrumentAssetResolver, CollectionIbInstrumentAssetResolver>();

        services.AddSingleton<IVenueConnector>(sp => new IbVenueConnector(
            sp.GetRequiredService<IIbMarketDataSession>(),
            sp.GetRequiredService<IIbContractResolver>(),
            sp.GetRequiredService<IIbInstrumentAssetResolver>(),
            sp.GetRequiredService<IbDataPlaneOptions>()));

        services.AddSingleton<IBackfillRequester>(sp => new IbBackfillRequester(
            sp.GetRequiredService<IIbHistoricalTicksClient>(),
            sp.GetRequiredService<IFileStorage>(),
            sp.GetRequiredService<CatchupOptions>().RelayKeyPrefix,
            sp.GetRequiredService<IbDataPlaneOptions>(),
            sp.GetRequiredService<IIbContractResolver>(),
            sp.GetRequiredService<IIbInstrumentAssetResolver>(),
            sp.GetRequiredService<TimeProvider>()));

        services.AddSingleton<IBarSourceResolver>(sp => new IbBarSourceResolver(
            sp.GetRequiredService<IIbMarketDataSession>(),
            sp.GetRequiredService<IIbContractResolver>(),
            sp.GetRequiredService<IReplaySource>(),
            sp.GetRequiredService<IBackfillRequester>(),
            sp.GetRequiredService<IInt64BarLoader>(),
            sp.GetRequiredService<IbDataPlaneOptions>(),
            sp.GetRequiredService<CatchupOptions>()));

        return services;
    }
}
