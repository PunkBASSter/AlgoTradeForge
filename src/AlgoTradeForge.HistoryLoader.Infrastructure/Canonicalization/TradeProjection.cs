using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Live.Relay;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Infrastructure.Storage.Buffered;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Canonicalization;

internal sealed class TradeProjection(ITickFeedWriter writer, InstrumentAssetDirMap map)
    : IStreamProjection<TradeTick>
{
    public void Apply(in TradeTick frame, in SegmentHeader header, SegmentLocation loc)
    {
        var assetDir = map.Resolve(loc.Venue, loc.InstrumentOrVenue);
        writer.Write(assetDir, new FeedRecord(frame.TimestampMs,
        [
            CanonicalScale.Unscale(frame.Price, header.PriceScaleExp),
            CanonicalScale.Unscale(frame.Quantity, header.QtyScaleExp),
            CanonicalScale.ToIsBuyerMaker(frame.Aggressor),
            frame.Sequence
        ]));
    }

    public Task Seed(SegmentLocation loc, CancellationToken ct) =>
        writer.ResumeFrom(map.Resolve(loc.Venue, loc.InstrumentOrVenue), ct);

    public Task Flush(CancellationToken ct) =>
        ((IBufferedPartitionWriter)writer).FlushAllAsync(ct);
}
