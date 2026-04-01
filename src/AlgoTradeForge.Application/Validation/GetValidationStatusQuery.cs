using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Application.Progress;

namespace AlgoTradeForge.Application.Validation;

public sealed record ValidationStatusDto
{
    public required Guid Id { get; init; }
    public required string Status { get; init; }
    public int CurrentStage { get; init; }
    public int TotalStages { get; init; } = ValidationPipeline.StageCount;
    public ValidationRunRecord? Result { get; init; }
}

public sealed record GetValidationStatusQuery(Guid Id) : IQuery<ValidationStatusDto?>;

public sealed class GetValidationStatusQueryHandler(
    RunProgressCache progressCache,
    IValidationRepository repository) : IQueryHandler<GetValidationStatusQuery, ValidationStatusDto?>
{
    public async Task<ValidationStatusDto?> HandleAsync(GetValidationStatusQuery query, CancellationToken ct = default)
    {
        // Check in-progress first
        var progress = await progressCache.GetProgressAsync(query.Id, ct);
        if (progress is not null)
        {
            return new ValidationStatusDto
            {
                Id = query.Id,
                Status = ValidationRunStatus.InProgress,
                CurrentStage = (int)progress.Value.Processed,
                TotalStages = (int)progress.Value.Total,
            };
        }

        // Fall back to completed record
        var record = await repository.GetByIdAsync(query.Id, ct);
        if (record is null) return null;

        // Detect orphaned run: DB says InProgress but no active progress in cache
        // means the background task died without updating the record.
        if (record.Status == ValidationRunStatus.InProgress)
        {
            var failed = record with
            {
                Status = ValidationRunStatus.Failed,
                CompletedAt = DateTimeOffset.UtcNow,
                DurationMs = (long)(DateTimeOffset.UtcNow - record.StartedAt).TotalMilliseconds,
                ErrorMessage = "Run did not complete — background task was interrupted.",
            };
            await repository.SaveAsync(failed, ct);
            return new ValidationStatusDto
            {
                Id = query.Id,
                Status = ValidationRunStatus.Failed,
                CurrentStage = 0,
                TotalStages = ValidationPipeline.StageCount,
                Result = failed,
            };
        }

        return new ValidationStatusDto
        {
            Id = query.Id,
            Status = record.Status,
            CurrentStage = record.StageResults.Count,
            TotalStages = ValidationPipeline.StageCount,
            Result = record,
        };
    }
}
