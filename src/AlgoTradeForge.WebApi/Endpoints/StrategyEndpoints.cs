using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Application.Strategies;
using AlgoTradeForge.Domain.Optimization.Attributes;
using AlgoTradeForge.Domain.Optimization.Space;
using AlgoTradeForge.WebApi.Contracts;

namespace AlgoTradeForge.WebApi.Endpoints;

public static class StrategyEndpoints
{
    public static void MapStrategyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/strategies")
            .WithTags("Strategies");

        group.MapGet("/", GetStrategies)
            .WithName("GetStrategies")
            .WithSummary("Get distinct strategy names from run history")
            .WithOpenApi()
            .Produces<IReadOnlyList<string>>(StatusCodes.Status200OK);

        group.MapGet("/available", GetAvailableStrategies)
            .WithName("GetAvailableStrategies")
            .WithSummary("Get all strategy descriptors discovered via reflection")
            .WithOpenApi()
            .Produces<IReadOnlyList<StrategyDescriptorResponse>>(StatusCodes.Status200OK);
    }

    private static async Task<IResult> GetStrategies(
        IQueryHandler<GetDistinctStrategyNamesQuery, IReadOnlyList<string>> handler,
        CancellationToken ct)
    {
        var names = await handler.HandleAsync(new GetDistinctStrategyNamesQuery(), ct);
        return Results.Ok(names);
    }

    private static async Task<IResult> GetAvailableStrategies(
        IQueryHandler<GetAvailableStrategiesQuery, IReadOnlyList<StrategyDescriptorDto>> handler,
        CancellationToken ct)
    {
        var descriptors = await handler.HandleAsync(new GetAvailableStrategiesQuery(), ct);
        var response = descriptors.Select(MapToResponse).ToList();
        return Results.Ok(response);
    }

    private static StrategyDescriptorResponse MapToResponse(StrategyDescriptorDto dto) => new()
    {
        Name = dto.Name,
        ParameterDefaults = new Dictionary<string, object>(dto.ParameterDefaults),
        OptimizationAxes = dto.Axes.Select(MapAxis).ToList(),
        BacktestTemplate = new Dictionary<string, object>(dto.BacktestTemplate),
        OptimizationTemplate = new Dictionary<string, object>(dto.OptimizationTemplate),
        LiveSessionTemplate = new Dictionary<string, object>(dto.LiveSessionTemplate),
        DebugSessionTemplate = new Dictionary<string, object>(dto.DebugSessionTemplate),
        GeneticOptimizationTemplate = new Dictionary<string, object>(dto.GeneticOptimizationTemplate),
    };

    private static ParameterAxisResponse MapAxis(ParameterAxis axis) => axis switch
    {
        NumericRangeAxis n => new ParameterAxisResponse
        {
            Name = n.Name,
            Type = "numeric",
            Min = n.Min,
            Max = n.Max,
            Step = n.Step,
            ClrType = MapClrTypeName(n.ClrType),
            Unit = n.Unit == ParamUnit.Raw ? null : char.ToLowerInvariant(n.Unit.ToString()[0]) + n.Unit.ToString()[1..],
        },
        DiscreteSetAxis d => new ParameterAxisResponse
        {
            Name = d.Name,
            Type = "discrete",
            ClrType = MapClrTypeName(d.ClrType),
            DiscreteValues = d.Values.Select(v => v.ToString()!).ToList(),
        },
        ModuleSlotAxis m => new ParameterAxisResponse
        {
            Name = m.Name,
            Type = "module",
            Variants = m.Variants.Select(v => new ModuleVariantResponse
            {
                TypeKey = v.TypeKey,
                Axes = v.Axes.Select(MapAxis).ToList(),
            }).ToList(),
        },
        _ => new ParameterAxisResponse
        {
            Name = axis.Name,
            Type = "unknown",
        },
    };

    private static string MapClrTypeName(Type type) => type.Name.ToLowerInvariant() switch
    {
        "decimal" => "decimal",
        "double" => "double",
        "int32" => "int",
        "int64" => "int64",
        _ => type.Name.ToLowerInvariant(),
    };
}
