namespace AlgoTradeForge.Live.Relay;

public static class RelayIngest
{
    // priceScaleExp/qtyScaleExp are placeholders; per-instrument scales come from config later.
    public static async Task Pump(IVenueConnector connector, RelayWriter writer, IReadOnlyList<string> instruments, CancellationToken ct = default)
    {
        var ids = new Dictionary<string, int>();
        foreach (var i in instruments) ids[i] = writer.RegisterInstrument(i, priceScaleExp: 2, qtyScaleExp: 0);
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
