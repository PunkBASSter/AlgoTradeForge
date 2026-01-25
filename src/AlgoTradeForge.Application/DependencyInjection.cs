using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Backtests;
using Microsoft.Extensions.DependencyInjection;

namespace AlgoTradeForge.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<ICommandHandler<RunBacktestCommand, BacktestResultDto>, RunBacktestCommandHandler>();

        return services;
    }
}
