namespace AlgoTradeForge.Domain.Validation.Results;

/// <summary>
/// Result of parameter sensitivity analysis: how much fitness degrades
/// when parameters are perturbed within a neighborhood.
/// </summary>
public sealed record ParameterSensitivityResult
{
    public required double MeanFitnessRetention { get; init; }
    public required IReadOnlyList<ParameterHeatmap> Heatmaps { get; init; }
    public required bool PassedDegradationCheck { get; init; }
}
