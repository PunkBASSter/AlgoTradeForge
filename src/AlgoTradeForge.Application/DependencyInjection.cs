using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Backtests;
using AlgoTradeForge.Application.Debug;
using AlgoTradeForge.Application.Events;
using AlgoTradeForge.Application.Live;
using AlgoTradeForge.Application.Optimization;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Application.Progress;
using AlgoTradeForge.Application.Strategies;
using AlgoTradeForge.Domain.Events;
using Microsoft.Extensions.DependencyInjection;

namespace AlgoTradeForge.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton<BacktestPreparer>();
        services.AddSingleton<ICommandHandler<RunBacktestCommand, BacktestSubmissionDto>, RunBacktestCommandHandler>();
        services.AddSingleton<ICommandHandler<RunOptimizationCommand, OptimizationSubmissionDto>, RunOptimizationCommandHandler>();
        services.AddSingleton<OptimizationAxisResolver>();

        // Progress tracking
        services.AddSingleton<RunProgressCache>();
        services.AddSingleton<IRunCancellationRegistry, InMemoryRunCancellationRegistry>();

        // Event bus (no-op by default; overridden when sinks are configured)
        services.AddSingleton<IEventBus>(NullEventBus.Instance);

        // Event log storage defaults
        services.Configure<EventLogStorageOptions>(_ => { });
        services.Configure<PostRunPipelineOptions>(_ => { });

        // Run timeout defaults
        services.Configure<RunTimeoutOptions>(_ => { });

        // Run persistence defaults
        services.Configure<RunStorageOptions>(_ => { });

        // Query handlers
        services.AddScoped<IQueryHandler<GetBacktestByIdQuery, BacktestRunRecord?>, GetBacktestByIdQueryHandler>();
        services.AddScoped<IQueryHandler<GetBacktestStatusQuery, BacktestStatusDto?>, GetBacktestStatusQueryHandler>();
        services.AddScoped<IQueryHandler<ListBacktestRunsQuery, PagedResult<BacktestRunRecord>>, ListBacktestRunsQueryHandler>();
        services.AddScoped<IQueryHandler<GetOptimizationByIdQuery, OptimizationRunRecord?>, GetOptimizationByIdQueryHandler>();
        services.AddScoped<IQueryHandler<GetOptimizationStatusQuery, OptimizationStatusDto?>, GetOptimizationStatusQueryHandler>();
        services.AddScoped<IQueryHandler<ListOptimizationRunsQuery, PagedResult<OptimizationRunRecord>>, ListOptimizationRunsQueryHandler>();
        services.AddScoped<IQueryHandler<GetDistinctStrategyNamesQuery, IReadOnlyList<string>>, GetDistinctStrategyNamesQueryHandler>();
        services.AddScoped<IQueryHandler<GetAvailableStrategiesQuery, IReadOnlyList<StrategyDescriptorDto>>, GetAvailableStrategiesQueryHandler>();
        services.AddScoped<ICommandHandler<DeleteOptimizationCommand, bool>, DeleteOptimizationCommandHandler>();
        services.AddScoped<ICommandHandler<CancelRunCommand, bool>, CancelRunCommandHandler>();

        // Debug session management
        services.AddSingleton<IDebugSessionStore, InMemoryDebugSessionStore>();
        services.AddScoped<ICommandHandler<StartDebugSessionCommand, DebugSessionDto>, StartDebugSessionCommandHandler>();
        services.AddScoped<ICommandHandler<SendDebugCommandRequest, DebugStepResultDto>, SendDebugCommandHandler>();

        // Live trading
        services.AddSingleton<ILiveSessionStore, InMemoryLiveSessionStore>();
        services.AddScoped<ICommandHandler<StartLiveSessionCommand, LiveSessionSubmissionDto>, StartLiveSessionCommandHandler>();
        services.AddScoped<ICommandHandler<StopLiveSessionCommand, bool>, StopLiveSessionCommandHandler>();
        services.AddScoped<IQueryHandler<GetLiveSessionDataQuery, LiveSessionDataDto?>, GetLiveSessionDataQueryHandler>();

        return services;
    }
}
