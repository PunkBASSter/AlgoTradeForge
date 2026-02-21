using AlgoTradeForge.Application.Abstractions;

namespace AlgoTradeForge.Application.Persistence;

public sealed record GetDistinctStrategyNamesQuery : ICommand<IReadOnlyList<string>>;

public sealed class GetDistinctStrategyNamesQueryHandler(
    IRunRepository repository) : ICommandHandler<GetDistinctStrategyNamesQuery, IReadOnlyList<string>>
{
    public Task<IReadOnlyList<string>> HandleAsync(GetDistinctStrategyNamesQuery query, CancellationToken ct = default)
        => repository.GetDistinctStrategyNamesAsync(ct);
}
