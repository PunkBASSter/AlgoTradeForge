using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Application.Progress;

namespace AlgoTradeForge.Application.Optimization;

public sealed record OptimizationGroupStatusDto
{
    public required Guid Id { get; init; }
    public required string Status { get; init; }
    public required IReadOnlyList<GroupRunProgressDto> Runs { get; init; }
}

public sealed record GroupRunProgressDto
{
    public required Guid Id { get; init; }
    public required string Status { get; init; }
    public long Processed { get; init; }
    public long Total { get; init; }
}

public sealed record GetOptimizationGroupStatusQuery(Guid GroupId) : IQuery<OptimizationGroupStatusDto?>;

public sealed class GetOptimizationGroupStatusQueryHandler(
    IRunRepository repository,
    RunProgressCache progressCache) : IQueryHandler<GetOptimizationGroupStatusQuery, OptimizationGroupStatusDto?>
{
    public async Task<OptimizationGroupStatusDto?> HandleAsync(
        GetOptimizationGroupStatusQuery query, CancellationToken ct = default)
    {
        var group = await repository.GetOptimizationGroupByIdAsync(query.GroupId, ct);
        if (group is null)
            return null;

        var runs = new List<GroupRunProgressDto>(group.Runs.Count);
        foreach (var run in group.Runs)
        {
            var progress = await progressCache.GetProgressAsync(run.Id, ct);
            runs.Add(new GroupRunProgressDto
            {
                Id = run.Id,
                Status = run.Status,
                Processed = progress?.Processed
                    ?? (run.Status == OptimizationRunStatus.Enqueued ? 0 : run.TotalCombinations),
                Total = progress?.Total ?? run.TotalCombinations,
            });
        }

        return new OptimizationGroupStatusDto
        {
            Id = group.Id,
            Status = group.Status,
            Runs = runs,
        };
    }
}
