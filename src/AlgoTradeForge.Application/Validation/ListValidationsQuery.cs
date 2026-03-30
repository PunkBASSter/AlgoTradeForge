using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Persistence;

namespace AlgoTradeForge.Application.Validation;

public sealed record ListValidationsQuery(ValidationRunQuery Filter) : IQuery<PagedResult<ValidationRunRecord>>;

public sealed class ListValidationsQueryHandler(
    IValidationRepository repository) : IQueryHandler<ListValidationsQuery, PagedResult<ValidationRunRecord>>
{
    public Task<PagedResult<ValidationRunRecord>> HandleAsync(ListValidationsQuery query, CancellationToken ct = default)
        => repository.QueryAsync(query.Filter, ct);
}
