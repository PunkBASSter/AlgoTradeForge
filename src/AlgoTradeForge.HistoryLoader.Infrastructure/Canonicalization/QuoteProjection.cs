using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Live.Relay;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Infrastructure.Storage.Buffered;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Canonicalization;

internal sealed class QuoteProjection(IBookTickerWriter writer, InstrumentAssetDirMap map)
    : IStreamProjection<QuoteTick>
{
    public void Apply(in QuoteTick frame, in SegmentHeader header, SegmentLocation loc)
    {
        var assetDir = map.Resolve(loc.Venue, loc.InstrumentOrVenue);
        writer.Write(assetDir, new FeedRecord(frame.TimestampMs,
        [
            CanonicalScale.Unscale(frame.BidPrice, header.PriceScaleExp),
            CanonicalScale.Unscale(frame.BidSize, header.QtyScaleExp),
            CanonicalScale.Unscale(frame.AskPrice, header.PriceScaleExp),
            CanonicalScale.Unscale(frame.AskSize, header.QtyScaleExp),
            frame.Sequence
        ]));
    }

    public Task Seed(SegmentLocation loc, CancellationToken ct)
    {
        map.BeginSession(); // snapshot the plan for the whole session (Seed precedes all Applies)
        return writer.ResumeFrom(map.Resolve(loc.Venue, loc.InstrumentOrVenue), ct);
    }

    public Task Flush(CancellationToken ct) =>
        ((IBufferedPartitionWriter)writer).FlushAllAsync(ct);
}
