using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Live.Relay;
using AlgoTradeForge.Storage;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Canonicalization;

internal sealed class StreamCanonicalizer<T>(
    IFileStorage storage,
    IStreamProjection<T> projection,
    IStreamCursorStore cursors,
    string liveMdPrefix,
    string cursorPrefix) : IStreamCanonicalizer
    where T : IFramePayload<T>
{
    public string StreamName => T.StreamName;

    public async Task<int> Run(string venue, string instrumentOrVenue, CancellationToken ct = default)
    {
        var streamPrefix = $"{liveMdPrefix}/{venue}/{instrumentOrVenue}/{T.StreamName}/";
        var cursorKey = $"{cursorPrefix}/{venue}/{instrumentOrVenue}/{T.StreamName}.cursor";

        var cursor = await cursors.Read(cursorKey, ct);

        var keys = new List<string>();
        await foreach (var k in storage.ListKeys(streamPrefix, ".atft", recursive: true, ct))
            keys.Add(k);
        keys.Sort(StringComparer.Ordinal);

        var pending = cursor.LastSegmentKey is { } last
            ? keys.Where(k => string.CompareOrdinal(k, last) > 0).ToList()
            : keys;
        if (pending.Count == 0) return 0;

        var etag = cursor.ETag;
        int frames = 0;
        bool seeded = false;
        foreach (var key in pending)
        {
            ct.ThrowIfCancellationRequested();
            if (!SegmentKeyParser.TryParse(key, liveMdPrefix, out var loc)) continue;

            if (!seeded)
            {
                // Seed the writer dedup watermark once, strictly before the first Apply,
                // so a reprocessed boundary segment (crash between flush and cursor-advance) is a no-op.
                await projection.Seed(loc, ct);
                seeded = true;
            }

            using (var reader = new SegmentReader<T>(await storage.OpenRead(key, ct)))
            {
                while (reader.TryRead(out var frame))
                {
                    projection.Apply(frame, reader.Header, loc);
                    frames++;
                }
            }

            await projection.Flush(ct);                              // durable publish ...
            etag = await cursors.Advance(cursorKey, key, etag, ct);  // ... then advance the cursor
        }
        return frames;
    }
}
