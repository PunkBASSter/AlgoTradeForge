namespace AlgoTradeForge.Domain.Validation.Results;

/// <summary>
/// Walk-forward matrix result: a grid of WFO results across different period counts and OOS percentages.
/// </summary>
public sealed record WfmResult
{
    /// <summary>Grid[periodIdx][oosIdx] — null if WFO failed to produce a result for that cell.</summary>
    public required WfoResult?[][] Grid { get; init; }
    public required int[] PeriodCounts { get; init; }
    public required double[] OosPcts { get; init; }

    /// <summary>Largest contiguous rectangle of passing cells, or null if none meets minimum size.</summary>
    public (int Row, int Col, int Rows, int Cols)? LargestContiguousCluster { get; init; }
    public int ClusterPassCount { get; init; }
    public int? OptimalReoptPeriod { get; init; }
}
