using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Persistence;

namespace AlgoTradeForge.Application.Validation;

public sealed record GetValidationGroupByIdQuery(Guid Id) : IQuery<ValidationGroupRecord?>;

public sealed class GetValidationGroupByIdQueryHandler(
    IValidationRepository repository) : IQueryHandler<GetValidationGroupByIdQuery, ValidationGroupRecord?>
{
    public Task<ValidationGroupRecord?> HandleAsync(GetValidationGroupByIdQuery query, CancellationToken ct = default)
        => repository.GetValidationGroupByIdAsync(query.Id, ct);
}
