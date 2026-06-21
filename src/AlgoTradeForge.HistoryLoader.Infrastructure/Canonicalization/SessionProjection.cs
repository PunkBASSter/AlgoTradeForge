using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Live.Relay;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Infrastructure.Storage.Buffered;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Canonicalization;

internal sealed class SessionProjection(ISessionFeedWriter writer, InstrumentAssetDirMap map)
    : IStreamProjection<SessionEvent>
{
    public void Apply(in SessionEvent frame, in SegmentHeader header, SegmentLocation loc)
    {
        var venueDir = map.VenueDir(loc.Venue);
        writer.Write(venueDir, new FeedRecord(frame.TimestampMs, [(double)(int)frame.Kind]));
    }

    public Task Seed(SegmentLocation loc, CancellationToken ct) =>
        writer.ResumeFrom(map.VenueDir(loc.Venue), ct);

    public Task Flush(CancellationToken ct) =>
        ((IBufferedPartitionWriter)writer).FlushAllAsync(ct);
}
