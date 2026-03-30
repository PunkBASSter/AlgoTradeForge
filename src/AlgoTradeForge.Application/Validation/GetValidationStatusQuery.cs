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
