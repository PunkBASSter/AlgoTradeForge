using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Backtests;
using AlgoTradeForge.Application.Optimization;
using Microsoft.Extensions.DependencyInjection;

namespace AlgoTradeForge.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<ICommandHandler<RunBacktestCommand, BacktestResultDto>, RunBacktestCommandHandler>();
        services.AddScoped<ICommandHandler<RunOptimizationCommand, OptimizationResultDto>, RunOptimizationCommandHandler>();
        services.AddSingleton<OptimizationAxisResolver>();

        return services;
    }
}
