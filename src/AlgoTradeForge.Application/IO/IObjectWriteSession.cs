namespace AlgoTradeForge.Application.IO;

/// <summary>
/// Callers MUST call <see cref="Commit"/> to publish; <see cref="IAsyncDisposable.DisposeAsync"/>
/// without commit aborts, so cancellation mid-write cannot leak partial data. Double-Commit and
/// double-Abort are no-ops; Commit after Abort throws.
/// </summary>
public interface IObjectWriteSession : IAsyncDisposable
{
    Stream Stream { get; }
    Task Commit(CancellationToken ct = default);
    Task Abort(CancellationToken ct = default);
}
