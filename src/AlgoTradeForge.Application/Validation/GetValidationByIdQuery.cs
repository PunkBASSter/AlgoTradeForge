using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Persistence;

namespace AlgoTradeForge.Application.Validation;

public sealed record GetValidationByIdQuery(Guid Id) : IQuery<ValidationRunRecord?>;

public sealed class GetValidationByIdQueryHandler(
    IValidationRepository repository) : IQueryHandler<GetValidationByIdQuery, ValidationRunRecord?>
{
    public Task<ValidationRunRecord?> HandleAsync(GetValidationByIdQuery query, CancellationToken ct = default)
        => repository.GetByIdAsync(query.Id, ct);
}
