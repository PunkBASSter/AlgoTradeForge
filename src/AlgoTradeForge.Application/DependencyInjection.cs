using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Backtests;
using AlgoTradeForge.Application.Debug;
using AlgoTradeForge.Application.Events;
using AlgoTradeForge.Application.Optimization;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Application.Progress;
using AlgoTradeForge.Application.Strategies;
using AlgoTradeForge.Application.Validation;
using AlgoTradeForge.Domain.Events;
using Microsoft.Extensions.DependencyInjection;

namespace AlgoTradeForge.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton<BacktestPreparer>();
        services.AddSingleton<ICommandHandler<RunBacktestCommand, BacktestSubmissionDto>, RunBacktestCommandHandler>();
        services.AddSingleton<OptimizationSetupHelper>();
        services.AddSingleton<ICommandHandler<RunGeneticOptimizationCommand, OptimizationSubmissionDto>, RunGeneticOptimizationCommandHandler>();
        services.AddSingleton<ICommandHandler<RunGroupOptimizationCommand, OptimizationGroupSubmissionDto>, RunGroupOptimizationCommandHandler>();
        services.AddSingleton<OptimizationAxisResolver>();

        // Compute task queue + executors
        services.AddSingleton<ComputeTaskQueue>();
        services.AddSingleton<OptimizationTaskExecutor>();
        services.AddSingleton<GeneticOptimizationTaskExecutor>();
        services.AddSingleton<ValidationTaskExecutor>();

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
        services.AddScoped<IQueryHandler<GetOptimizationTrialsQuery, PagedResult<BacktestRunRecord>>, GetOptimizationTrialsQueryHandler>();
        services.AddScoped<IQueryHandler<GetOptimizationStatusQuery, OptimizationStatusDto?>, GetOptimizationStatusQueryHandler>();
        services.AddScoped<IQueryHandler<ListOptimizationRunsQuery, PagedResult<OptimizationRunRecord>>, ListOptimizationRunsQueryHandler>();
        services.AddScoped<IQueryHandler<EvaluateOptimizationQuery, OptimizationEvaluationDto>, EvaluateOptimizationQueryHandler>();
        services.AddScoped<IQueryHandler<GetDistinctStrategyNamesQuery, IReadOnlyList<string>>, GetDistinctStrategyNamesQueryHandler>();
        services.AddScoped<IQueryHandler<GetAvailableStrategiesQuery, IReadOnlyList<StrategyDescriptorDto>>, GetAvailableStrategiesQueryHandler>();
        services.AddScoped<ICommandHandler<DeleteOptimizationCommand, bool>, DeleteOptimizationCommandHandler>();
        services.AddScoped<ICommandHandler<DeleteBacktestCommand, bool>, DeleteBacktestCommandHandler>();
        services.AddScoped<ICommandHandler<CancelRunCommand, bool>, CancelRunCommandHandler>();

        // Optimization group handlers
        services.AddScoped<IQueryHandler<GetOptimizationGroupByIdQuery, OptimizationGroupRecord?>, GetOptimizationGroupByIdQueryHandler>();
        services.AddScoped<IQueryHandler<GetOptimizationGroupTrialsQuery, PagedResult<BacktestRunRecord>>, GetOptimizationGroupTrialsQueryHandler>();
        services.AddScoped<IQueryHandler<GetOptimizationGroupStatusQuery, OptimizationGroupStatusDto?>, GetOptimizationGroupStatusQueryHandler>();
        services.AddScoped<ICommandHandler<CancelOptimizationGroupCommand, bool>, CancelOptimizationGroupCommandHandler>();
        services.AddScoped<ICommandHandler<DeleteOptimizationGroupCommand, bool>, DeleteOptimizationGroupCommandHandler>();

        // Validation
        services.AddSingleton<ICommandHandler<RunValidationCommand, ValidationSubmissionDto>, RunValidationCommandHandler>();
        services.AddSingleton<ICommandHandler<RunGroupValidationCommand, ValidationGroupSubmissionDto>, RunGroupValidationCommandHandler>();
        services.AddScoped<IQueryHandler<GetValidationByIdQuery, ValidationRunRecord?>, GetValidationByIdQueryHandler>();
        services.AddScoped<IQueryHandler<GetValidationStatusQuery, ValidationStatusDto?>, GetValidationStatusQueryHandler>();
        services.AddScoped<IQueryHandler<GetValidationEquityQuery, ValidationEquityDto?>, GetValidationEquityQueryHandler>();
        services.AddScoped<IQueryHandler<ListValidationsQuery, PagedResult<ValidationRunRecord>>, ListValidationsQueryHandler>();
        services.AddScoped<IQueryHandler<GetValidationGroupByIdQuery, ValidationGroupRecord?>, GetValidationGroupByIdQueryHandler>();
        services.AddScoped<IQueryHandler<GetValidationGroupStatusQuery, ValidationGroupStatusDto?>, GetValidationGroupStatusQueryHandler>();
        services.AddScoped<ICommandHandler<CancelValidationGroupCommand, bool>, CancelValidationGroupCommandHandler>();
        services.AddScoped<ICommandHandler<DeleteValidationGroupCommand, bool>, DeleteValidationGroupCommandHandler>();

        // Debug session management
        services.AddSingleton<IDebugSessionStore, InMemoryDebugSessionStore>();
        services.AddScoped<ICommandHandler<StartDebugSessionCommand, DebugSessionDto>, StartDebugSessionCommandHandler>();
        services.AddScoped<ICommandHandler<SendDebugCommandRequest, DebugStepResultDto>, SendDebugCommandHandler>();

        return services;
    }
}
