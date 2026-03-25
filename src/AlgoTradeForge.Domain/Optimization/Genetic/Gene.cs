using AlgoTradeForge.Domain.Optimization.Space;

namespace AlgoTradeForge.Domain.Optimization.Genetic;

/// <summary>
/// Abstract gene — one dimension of a chromosome.
/// </summary>
public abstract record Gene;

/// <summary>
/// Continuous numeric gene. Value stored as double for crossover math;
/// snapped to step grid on materialization to ParameterCombination.
/// </summary>
public sealed record NumericGene(
    double Value,
    double Min,
    double Max,
    double Step,
    Type ClrType) : Gene;

/// <summary>
/// Discrete gene — indexes into a fixed list of allowed values.
/// </summary>
public sealed record DiscreteGene(
    int Index,
    IReadOnlyList<object> Choices) : Gene;

/// <summary>
/// Module gene — selects a variant and optionally carries sub-genes
/// for that variant's parameters. Different variants have incompatible
/// sub-parameter structures, so crossover must handle variant mismatch.
/// </summary>
public sealed record ModuleGene(
    int VariantIndex,
    IReadOnlyList<ResolvedModuleVariant> Variants,
    IReadOnlyDictionary<string, Gene>? SubGenes) : Gene;
