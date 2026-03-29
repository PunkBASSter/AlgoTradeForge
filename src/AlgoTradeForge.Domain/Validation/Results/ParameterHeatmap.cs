namespace AlgoTradeForge.Domain.Validation.Results;

/// <summary>
/// 2D heatmap of fitness values across two parameter axes.
/// Used to visualize parameter sensitivity and identify plateaus.
/// </summary>
public sealed record ParameterHeatmap
{
    public required string Param1Name { get; init; }
    public required string Param2Name { get; init; }
    public required double[] Param1Values { get; init; }
    public required double[] Param2Values { get; init; }

    /// <summary>FitnessGrid[i,j] = average fitness for (Param1Values[i], Param2Values[j]).</summary>
    public required double[,] FitnessGrid { get; init; }

    /// <summary>Fraction of grid cells within (1 - maxDegradation) of peak fitness.</summary>
    public required double PlateauScore { get; init; }
}
