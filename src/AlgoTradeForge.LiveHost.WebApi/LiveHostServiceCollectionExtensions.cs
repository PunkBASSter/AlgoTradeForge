using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Domain.Live;
using AlgoTradeForge.LiveHost.Application.Live;
using AlgoTradeForge.LiveHost.Infrastructure.Live.Binance;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AlgoTradeForge.LiveHost.WebApi;

public static class LiveHostServiceCollectionExtensions
{
    public static IServiceCollection AddLiveHost(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<BinanceLiveOptions>(config.GetSection("BinanceLive"));
        services.AddSingleton<ILiveSessionStore, InMemoryLiveSessionStore>();
        services.AddSingleton<ILiveAccountManager, BinanceLiveAccountManager>();
        services.AddSingleton<ILiveSessionDataProvider, BinanceLiveSessionDataProvider>();
        services.AddScoped<ICommandHandler<StartLiveSessionCommand, LiveSessionSubmissionDto>, StartLiveSessionCommandHandler>();
        services.AddScoped<ICommandHandler<StopLiveSessionCommand, bool>, StopLiveSessionCommandHandler>();
        services.AddScoped<IQueryHandler<GetLiveSessionDataQuery, LiveSessionDataDto?>, GetLiveSessionDataQueryHandler>();
        return services;
    }
}
