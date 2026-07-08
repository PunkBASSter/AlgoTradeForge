using System.Collections.Concurrent;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Live.Relay;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Infrastructure.Storage.Buffered;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Canonicalization;

internal sealed class TradeProjection(
    ITickFeedWriter writer, InstrumentAssetDirMap map, ILogger<TradeProjection> logger)
    : IStreamProjection<TradeTick>
{
    private readonly ConcurrentDictionary<string, byte> _warnedFallback = new();

    public void Apply(in TradeTick frame, in SegmentHeader header, SegmentLocation loc)
    {
        var assetDir = map.Resolve(loc.Venue, loc.InstrumentOrVenue);
        // The writer scales the decimal magnitude by 10^digits (Task 3), sharing one code path with
        // the HistoryLoader tick callers. Unconfigured instruments fall back to the canonical price
        // exponent, which preserves the segment's already-scaled price long.
        var digits = map.ResolveDigits(loc.InstrumentOrVenue) ?? FallbackDigits(loc.InstrumentOrVenue, header);
        writer.Write(assetDir, new FeedRecord(frame.TimestampMs,
        [
            CanonicalScale.Unscale(frame.Price, header.PriceScaleExp),
            CanonicalScale.Unscale(frame.Quantity, header.QtyScaleExp),
            CanonicalScale.ToIsBuyerMaker(frame.Aggressor),
            frame.Sequence
        ]), digits);
    }

    private int FallbackDigits(string instrument, in SegmentHeader header)
    {
        if (_warnedFallback.TryAdd(instrument, 0))
            logger.LogWarning(
                "Canonicalizer: instrument {Instrument} has no configured DecimalDigits; " +
                "falling back to canonical PriceScaleExp {Exp}", instrument, header.PriceScaleExp);
        return header.PriceScaleExp;
    }

    public Task Seed(SegmentLocation loc, CancellationToken ct) =>
        writer.ResumeFrom(map.Resolve(loc.Venue, loc.InstrumentOrVenue), ct);

    public Task Flush(CancellationToken ct) =>
        ((IBufferedPartitionWriter)writer).FlushAllAsync(ct);
}
