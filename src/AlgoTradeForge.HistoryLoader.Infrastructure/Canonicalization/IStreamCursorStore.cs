namespace AlgoTradeForge.HistoryLoader.Infrastructure.Canonicalization;

/// <summary>The last fully-consumed segment key for one stream, plus its CAS etag.</summary>
public readonly record struct StreamCursor(string? LastSegmentKey, string? ETag);

public interface IStreamCursorStore
{
    Task<StreamCursor> Read(string cursorKey, CancellationToken ct = default);

    /// <summary>Advances the cursor under CAS. Pass the etag from <see cref="Read"/> (or null
    /// for create). Throws <see cref="AlgoTradeForge.Storage.ConcurrencyConflictException"/> on
    /// a stale etag. Returns the new etag.</summary>
    Task<string> Advance(string cursorKey, string lastSegmentKey, string? expectedETag, CancellationToken ct = default);
}
