using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Application.Progress;

namespace AlgoTradeForge.Application.Validation;

public sealed record ValidationGroupStatusDto
{
    public required Guid Id { get; init; }
    public required string Status { get; init; }
    public required IReadOnlyList<ValidationGroupRunProgressDto> Runs { get; init; }
}

public sealed record ValidationGroupRunProgressDto
{
    public required Guid Id { get; init; }
    public required string Status { get; init; }
    public long Processed { get; init; }
    public long Total { get; init; }
}

public sealed record GetValidationGroupStatusQuery(Guid GroupId) : IQuery<ValidationGroupStatusDto?>;

public sealed class GetValidationGroupStatusQueryHandler(
    IValidationRepository repository,
    RunProgressCache progressCache) : IQueryHandler<GetValidationGroupStatusQuery, ValidationGroupStatusDto?>
{
    public async Task<ValidationGroupStatusDto?> HandleAsync(
        GetValidationGroupStatusQuery query, CancellationToken ct = default)
    {
        var group = await repository.GetValidationGroupByIdAsync(query.GroupId, ct);
        if (group is null)
            return null;

        var runs = new List<ValidationGroupRunProgressDto>(group.Runs.Count);
        foreach (var run in group.Runs)
        {
            var progress = await progressCache.GetProgressAsync(run.Id, ct);
            runs.Add(new ValidationGroupRunProgressDto
            {
                Id = run.Id,
                Status = run.Status,
                Processed = progress?.Processed ?? (run.Status == ValidationRunStatus.InProgress ? 0 : 1),
                Total = progress?.Total ?? 1,
            });
        }

        return new ValidationGroupStatusDto
        {
            Id = group.Id,
            Status = group.Status,
            Runs = runs,
        };
    }
}
