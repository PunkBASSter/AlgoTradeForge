using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Live.Relay;
using AlgoTradeForge.LiveHost.Infrastructure.Live.Binance;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// IB tick lane: resolves each instrument's contract, subscribes tick-by-tick on the shared session,
// and bridges the EWrapper push callbacks through a bounded channel into the pull IVenueConnector seam.
// Independent price/qty scaling via configured TickScale exponents (mirrors BinanceVenueConnector).
internal sealed class IbVenueConnector(
    IIbMarketDataSession session,
    IIbContractResolver resolver,
    IIbInstrumentAssetResolver assets,
    IbDataPlaneOptions options) : IVenueConnector
{
    public string Venue => "ib";
    public MarketDataSessionPolicy SessionPolicy => MarketDataSessionPolicy.SingleSession;

    public (sbyte PriceScaleExp, sbyte QtyScaleExp) InstrumentScale(string instrument)
    {
        var s = ScaleFor(instrument);
        return ((sbyte)s.PriceExp, (sbyte)s.QtyExp);
    }

    public async IAsyncEnumerable<IMarketEvent> Stream(
        IReadOnlyList<string> instruments,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = Channel.CreateBounded<IMarketEvent>(
            new BoundedChannelOptions(options.IngestChannelCapacity) { SingleReader = true });

        await session.Connect(ct).ConfigureAwait(false);

        var reqIds = new List<int>(instruments.Count);
        foreach (var instrument in instruments)
        {
            var scale = ScaleFor(instrument);
            var seq = new SyntheticSequence();
            var asset = await assets.Resolve(instrument, ct).ConfigureAwait(false);
            var resolved = await resolver.Resolve(asset.ToIbContract(), ct).ConfigureAwait(false);
            reqIds.Add(session.SubscribeTrades(resolved, update =>
                channel.Writer.TryWrite(ToTradeEvent(instrument, update, scale, seq.Next()))));
        }

        try
        {
            await foreach (var ev in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return ev;
        }
        finally
        {
            // Drop the subscriptions when the consumer stops/cancels, so the session does not retain dead
            // subs and re-issue them (into this now-abandoned channel) on the next reconnect.
            foreach (var id in reqIds)
                session.Unsubscribe(id);
        }
    }

    internal static TradeEvent ToTradeEvent(string instrument, IbTradeUpdate u, TickScale scale, long sequence)
    {
        var tick = new TradeTick(
            TimestampMs: u.TimeSec * 1000,
            Price: scale.ScalePrice((decimal)u.Price),
            Quantity: scale.ScaleQty(u.Size),
            Sequence: sequence,
            Aggressor: AggressorSide.Unknown);
        return new TradeEvent(instrument, tick);
    }

    private TickScale ScaleFor(string instrument) =>
        options.InstrumentScales.TryGetValue(instrument, out var s) ? s : options.DefaultScale;

    // Per-instrument monotonic synthetic sequence (IB carries none). Single-writer per instrument (the pump thread).
    private sealed class SyntheticSequence { private long _n; public long Next() => Interlocked.Increment(ref _n); }
}
