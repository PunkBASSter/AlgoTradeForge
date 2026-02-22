using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Application.Progress;

namespace AlgoTradeForge.Application.Backtests;

public sealed record BacktestStatusDto
{
    public required Guid Id { get; init; }
    public long ProcessedBars { get; init; }
    public long TotalBars { get; init; }
    public BacktestRunRecord? Result { get; init; }
}

public sealed record GetBacktestStatusQuery(Guid Id) : ICommand<BacktestStatusDto?>;

public sealed class GetBacktestStatusQueryHandler(
    RunProgressCache progressCache,
    IRunRepository repository) : ICommandHandler<GetBacktestStatusQuery, BacktestStatusDto?>
{
    public async Task<BacktestStatusDto?> HandleAsync(GetBacktestStatusQuery query, CancellationToken ct = default)
    {
        var progress = await progressCache.GetProgressAsync(query.Id, ct);
        if (progress is not null)
        {
            return new BacktestStatusDto
            {
                Id = query.Id,
                ProcessedBars = progress.Value.Processed,
                TotalBars = progress.Value.Total,
            };
        }

        var record = await repository.GetByIdAsync(query.Id, ct);
        if (record is not null)
        {
            return new BacktestStatusDto
            {
                Id = query.Id,
                ProcessedBars = record.TotalBars,
                TotalBars = record.TotalBars,
                Result = record,
            };
        }

        return null;
    }
}
