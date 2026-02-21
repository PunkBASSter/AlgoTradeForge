using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Backtests;
using AlgoTradeForge.Application.Debug;
using AlgoTradeForge.Application.Events;
using AlgoTradeForge.Application.Optimization;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Domain.Events;
using Microsoft.Extensions.DependencyInjection;

namespace AlgoTradeForge.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<BacktestPreparer>();
        services.AddScoped<ICommandHandler<RunBacktestCommand, BacktestResultDto>, RunBacktestCommandHandler>();
        services.AddScoped<ICommandHandler<RunOptimizationCommand, OptimizationResultDto>, RunOptimizationCommandHandler>();
        services.AddSingleton<OptimizationAxisResolver>();

        // Event bus (no-op by default; overridden when sinks are configured)
        services.AddSingleton<IEventBus>(NullEventBus.Instance);

        // Event log storage defaults
        services.Configure<EventLogStorageOptions>(_ => { });
        services.Configure<PostRunPipelineOptions>(_ => { });

        // Run persistence defaults
        services.Configure<RunStorageOptions>(_ => { });

        // Query handlers
        services.AddScoped<ICommandHandler<GetBacktestByIdQuery, BacktestRunRecord?>, GetBacktestByIdQueryHandler>();
        services.AddScoped<ICommandHandler<ListBacktestRunsQuery, PagedResult<BacktestRunRecord>>, ListBacktestRunsQueryHandler>();
        services.AddScoped<ICommandHandler<GetOptimizationByIdQuery, OptimizationRunRecord?>, GetOptimizationByIdQueryHandler>();
        services.AddScoped<ICommandHandler<ListOptimizationRunsQuery, PagedResult<OptimizationRunRecord>>, ListOptimizationRunsQueryHandler>();
        services.AddScoped<ICommandHandler<GetDistinctStrategyNamesQuery, IReadOnlyList<string>>, GetDistinctStrategyNamesQueryHandler>();

        // Debug session management
        services.AddSingleton<IDebugSessionStore, InMemoryDebugSessionStore>();
        services.AddScoped<ICommandHandler<StartDebugSessionCommand, DebugSessionDto>, StartDebugSessionCommandHandler>();
        services.AddScoped<ICommandHandler<SendDebugCommandRequest, DebugStepResultDto>, SendDebugCommandHandler>();

        return services;
    }
}
