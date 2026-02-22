using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Application.Progress;

namespace AlgoTradeForge.Application.Optimization;

public sealed record OptimizationStatusDto
{
    public required Guid Id { get; init; }
    public long CompletedCombinations { get; init; }
    public long TotalCombinations { get; init; }
    public OptimizationRunRecord? Result { get; init; }
}

public sealed record GetOptimizationStatusQuery(Guid Id) : ICommand<OptimizationStatusDto?>;

public sealed class GetOptimizationStatusQueryHandler(
    RunProgressCache progressCache,
    IRunRepository repository) : ICommandHandler<GetOptimizationStatusQuery, OptimizationStatusDto?>
{
    public async Task<OptimizationStatusDto?> HandleAsync(GetOptimizationStatusQuery query, CancellationToken ct = default)
    {
        var progress = await progressCache.GetProgressAsync(query.Id, ct);
        if (progress is not null)
        {
            return new OptimizationStatusDto
            {
                Id = query.Id,
                CompletedCombinations = progress.Value.Processed,
                TotalCombinations = progress.Value.Total,
            };
        }

        var record = await repository.GetOptimizationByIdAsync(query.Id, ct);
        if (record is not null)
        {
            return new OptimizationStatusDto
            {
                Id = query.Id,
                CompletedCombinations = record.TotalCombinations,
                TotalCombinations = record.TotalCombinations,
                Result = record,
            };
        }

        return null;
    }
}
