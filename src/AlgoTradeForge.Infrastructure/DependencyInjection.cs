using System.Reflection;
using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Events;
using AlgoTradeForge.Application.IO;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Domain.Live;
using AlgoTradeForge.Domain.Optimization;
using AlgoTradeForge.Infrastructure.Events;
using AlgoTradeForge.Infrastructure.IO;
using AlgoTradeForge.Application.Live;
using AlgoTradeForge.Infrastructure.Live.Binance;
using AlgoTradeForge.Infrastructure.Optimization;
using AlgoTradeForge.Application.Validation;
using AlgoTradeForge.Infrastructure.Persistence;
using AlgoTradeForge.Infrastructure.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, params Assembly[] assemblies)
    {
        var builder = new SpaceDescriptorBuilder(assemblies);

        services.AddSingleton(builder);
        services.AddSingleton<IOptimizationSpaceProvider>(sp => sp.GetRequiredService<SpaceDescriptorBuilder>());
        services.AddSingleton<ICartesianProductGenerator, CartesianProductGenerator>();

        var factory = new OptimizationStrategyFactory(builder);
        services.AddSingleton<IStrategyFactory>(factory);
        services.AddSingleton<IOptimizationStrategyFactory>(factory);

        services.Configure<StorageOptions>(_ => { });
        services.AddSingleton<IFileStorage>(BuildFileStorage);
        services.AddSingleton<IPartitionTailIndex>(BuildTailIndex);
        services.AddSingleton<IRunSinkFactory, JsonlRunSinkFactory>();
        services.AddSingleton<IEventIndexBuilder, SqliteEventIndexBuilder>();
        services.AddSingleton<ITradeDbWriter, SqliteTradeDbWriter>();
        services.AddSingleton<IPostRunPipeline, PostRunPipeline>();

        services.AddSingleton<IRunRepository, SqliteRunRepository>();
        services.AddSingleton<IValidationRepository, SqliteValidationRepository>();
        services.AddSingleton<ISimulationCacheFileStore, SimulationCacheFileStore>();
        services.AddSingleton<IThresholdProfileRepository, SqliteThresholdProfileRepository>();

        // Live trading
        services.Configure<BinanceLiveOptions>(_ => { });
        services.AddSingleton<ILiveAccountManager, BinanceLiveAccountManager>();
        services.AddSingleton<ILiveSessionDataProvider, BinanceLiveSessionDataProvider>();

        return services;
    }

    internal static IFileStorage BuildFileStorage(IServiceProvider sp)
    {
        var opt = sp.GetRequiredService<IOptions<StorageOptions>>().Value;
        return opt.Backend switch
        {
            StorageBackend.S3 => new S3FileStorage(opt.S3, sp.GetRequiredService<ILogger<S3FileStorage>>()),
            _                 => new LocalFileStorage(opt.Local),
        };
    }

    internal static IPartitionTailIndex BuildTailIndex(IServiceProvider sp)
    {
        // The tail index has to know the backend layout — Local uses Seek(-N, End) on the
        // OpenRead stream; S3 issues a Range GET. They can't share a single implementation.
        var storage = sp.GetRequiredService<IFileStorage>();
        return storage switch
        {
            S3FileStorage s3 => new S3TailIndex(s3),
            _                => new LocalTailIndex(storage),
        };
    }
}
