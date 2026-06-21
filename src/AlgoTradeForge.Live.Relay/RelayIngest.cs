namespace AlgoTradeForge.Live.Relay;

public static class RelayIngest
{
    public static async Task Pump(IVenueConnector connector, RelayWriter writer, IReadOnlyList<string> instruments, CancellationToken ct = default)
    {
        var ids = new Dictionary<string, int>();
        foreach (var i in instruments)
        {
            var (p, q) = connector.InstrumentScale(i);
            ids[i] = writer.RegisterInstrument(i, priceScaleExp: p, qtyScaleExp: q);
        }
        await writer.Start(ct).ConfigureAwait(false);
        await foreach (var ev in connector.Stream(instruments, ct).ConfigureAwait(false))
        {
            switch (ev)
            {
                case TradeEvent t: await writer.WriteTrade(ids[t.Instrument], t.Tick, ct).ConfigureAwait(false); break;
                case QuoteEvent q: await writer.WriteQuote(ids[q.Instrument], q.Quote, ct).ConfigureAwait(false); break;
            }
        }
    }
}
