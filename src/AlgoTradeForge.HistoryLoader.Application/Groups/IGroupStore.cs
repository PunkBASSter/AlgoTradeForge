namespace AlgoTradeForge.HistoryLoader.Application.Groups;

public sealed record GroupDocument(CollectionGroup Group, string ETag);

public interface IGroupStore
{
    Task<IReadOnlyList<GroupDocument>> List(CancellationToken ct = default);
    Task<GroupDocument?> Get(string name, CancellationToken ct = default);

    /// <summary>CAS write. expectedETag null = create (must not exist). Validates name+structure
    /// (GroupValidator) BEFORE writing; throws GroupValidationException(errors) on failure and
    /// lets ConcurrencyConflictException from WriteIfMatch propagate. Returns new ETag.</summary>
    Task<string> Put(string name, CollectionGroup group, string? expectedETag, CancellationToken ct = default);

    Task<bool> Delete(string name, CancellationToken ct = default);

    /// <summary>Fires after every successful Put/Delete. Reconciler subscribes with debounce.
    /// Payload is deliberately empty — subscribers recompute over ALL groups; do not add args.</summary>
    event Action GroupsChanged;
}
