using AlgoTradeForge.Domain.Reporting;

namespace AlgoTradeForge.Domain.Validation.Stages;

public sealed record TrialSummary
{
    public required int Index { get; init; }
    public required Guid Id { get; init; }
    public required PerformanceMetrics Metrics { get; init; }
    public IReadOnlyDictionary<string, object>? Parameters { get; init; }
}
