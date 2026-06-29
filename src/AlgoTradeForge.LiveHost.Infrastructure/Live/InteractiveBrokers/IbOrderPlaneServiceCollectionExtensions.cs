using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.Live;
using AlgoTradeForge.LiveHost.Application.Live.DataPlane;
using AlgoTradeForge.LiveHost.Infrastructure.Live.Binance;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// Registers the IB order plane (the cohabiting half of AddIbDataPlane) and the single shared-connector
// ILiveAccountManager. Call this only for Venue=ib, AFTER AddIbDataPlane (it reuses the data plane's shared
// IbConnection/IbWrapper/IbSession singletons) and the venue-agnostic ITickRouter/IStrategyDispatch.
public static class IbOrderPlaneServiceCollectionExtensions
{
    public static IServiceCollection AddIbOrderPlane(this IServiceCollection services)
    {
        services.AddSingleton<IIbAccountSummaryClient, IbConnectionAccountSummaryClient>();

        services.AddSingleton<ILiveAccountManager>(sp => new IbLiveAccountManager(() =>
        {
            var connection = sp.GetRequiredService<IbConnection>();
            var wrapper = sp.GetRequiredService<IbWrapper>();
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var options = sp.GetRequiredService<IOptions<BinanceLiveOptions>>().Value.ToDispatcherOptions();

            return new IbLiveConnector(
                accountName: "ib",
                session: sp.GetRequiredService<IIbMarketDataSession>(),
                contractResolver: sp.GetRequiredService<IIbContractResolver>(),
                summaryClient: sp.GetRequiredService<IIbAccountSummaryClient>(),
                orderValidator: sp.GetRequiredService<IOrderValidator>(),
                tickRouter: sp.GetRequiredService<ITickRouter>(),
                dispatch: sp.GetRequiredService<IStrategyDispatch>(),
                options: options,
                loggerFactory: loggerFactory,
                gatewayFactory: onReport => new IbOrderGateway(
                    new IbConnectionOrderClient(connection),
                    wrapper,
                    onReport,
                    loggerFactory.CreateLogger<IbOrderGateway>()));
        }));

        return services;
    }

    // The dispatcher's capacities/cadence are shared LiveHost concerns. They currently live on BinanceLiveOptions
    // (the only options class with them); IB reuses those defaults until a venue-neutral options class lands.
    private static LiveDispatcherOptions ToDispatcherOptions(this BinanceLiveOptions o) =>
        new(o.LiveChannelCapacity, o.MarketDataChannelCapacity, o.ReconciliationInterval);
}
