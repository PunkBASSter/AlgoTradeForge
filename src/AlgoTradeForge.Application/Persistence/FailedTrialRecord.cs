namespace AlgoTradeForge.Application.Persistence;

public sealed record FailedTrialRecord
{
    public required Guid Id { get; init; }
    public required Guid OptimizationRunId { get; init; }
    public required string ExceptionType { get; init; }
    public required string ExceptionMessage { get; init; }
    public required string StackTrace { get; init; }
    public required IReadOnlyDictionary<string, object> SampleParameters { get; init; }
    public required long OccurrenceCount { get; init; }
}
