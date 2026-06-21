using AlgoTradeForge.Storage;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Canonicalization;

internal sealed class FileStreamCursorStore(IFileStorage storage) : IStreamCursorStore
{
    public async Task<StreamCursor> Read(string cursorKey, CancellationToken ct = default)
    {
        var obj = await storage.ReadWithEtag(cursorKey, ct);
        return obj is null
            ? new StreamCursor(null, null)
            : new StreamCursor(obj.Content.Trim(), obj.ETag);
    }

    public Task<string> Advance(string cursorKey, string lastSegmentKey, string? expectedETag, CancellationToken ct = default) =>
        storage.WriteIfMatch(cursorKey, lastSegmentKey, expectedETag, ct);
}
