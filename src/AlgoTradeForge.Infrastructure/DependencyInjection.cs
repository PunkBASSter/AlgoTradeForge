using System.Reflection;
using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Events;
using AlgoTradeForge.Domain.Optimization;
using AlgoTradeForge.Infrastructure.Events;
using AlgoTradeForge.Infrastructure.Optimization;
using Microsoft.Extensions.DependencyInjection;

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

        services.AddSingleton<IRunSinkFactory, JsonlRunSinkFactory>();

        return services;
    }
}
