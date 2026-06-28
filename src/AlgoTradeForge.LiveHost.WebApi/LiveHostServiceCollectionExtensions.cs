using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Domain.Live;
using AlgoTradeForge.LiveHost.Application.Collection;
using AlgoTradeForge.LiveHost.Application.Live;
using AlgoTradeForge.LiveHost.Infrastructure.Collection;
using AlgoTradeForge.LiveHost.Infrastructure.Live;
using AlgoTradeForge.LiveHost.Infrastructure.Live.Binance;
using AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AlgoTradeForge.LiveHost.WebApi;

public static class LiveHostServiceCollectionExtensions
{
    public static IServiceCollection AddLiveHost(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<BinanceLiveOptions>(config.GetSection("BinanceLive"));
        services.AddSingleton<ILiveSessionStore, InMemoryLiveSessionStore>();

        // ILiveAccountManager is venue-gated: IB is one shared connector over one socket (AddIbOrderPlane),
        // Binance is one connector per account. The data-provider snapshot path stays Binance-only for now.
        var venue = VenueSelector.Parse(config.GetValue<string>("Venue"));
        if (venue == VenueKind.Ib)
            services.AddIbOrderPlane();
        else
            services.AddSingleton<ILiveAccountManager, BinanceLiveAccountManager>();

        services.AddSingleton<ILiveSessionDataProvider, BinanceLiveSessionDataProvider>();
        services.AddScoped<ICommandHandler<StartLiveSessionCommand, LiveSessionSubmissionDto>, StartLiveSessionCommandHandler>();
        services.AddScoped<ICommandHandler<StopLiveSessionCommand, bool>, StopLiveSessionCommandHandler>();
        services.AddScoped<IQueryHandler<GetLiveSessionDataQuery, LiveSessionDataDto?>, GetLiveSessionDataQueryHandler>();
        services.AddSingleton<ICollectionConfigStore, CollectionConfigStore>();
        return services;
    }
}
