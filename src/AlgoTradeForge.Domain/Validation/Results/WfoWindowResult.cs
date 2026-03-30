namespace AlgoTradeForge.Domain.Validation.Results;

/// <summary>
/// Result for a single walk-forward window: IS optimization + OOS evaluation.
/// </summary>
public sealed record WfoWindowResult
{
    public required int WindowIndex { get; init; }
    public required int IsStartBar { get; init; }
    public required int IsEndBar { get; init; }
    public required int OosStartBar { get; init; }
    public required int OosEndBar { get; init; }
    public required WindowPerformanceMetrics IsMetrics { get; init; }
    public required WindowPerformanceMetrics OosMetrics { get; init; }
    public required int OptimalTrialIndex { get; init; }
    public required double WalkForwardEfficiency { get; init; }
    public required bool OosProfitable { get; init; }
}
