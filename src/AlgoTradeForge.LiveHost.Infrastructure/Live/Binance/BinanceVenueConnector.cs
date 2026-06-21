using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Live.Relay;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.Binance;

public sealed class BinanceVenueConnector(BinanceLiveOptions options, ILogger<BinanceVenueConnector> logger)
    : IVenueConnector
{
    // Default scale for instruments not declared in options: price ×100 (0.01 tick), qty ×100000 (0.00001 step)
    private static readonly TickScale DefaultTickScale = new(PriceExp: 2, QtyExp: 5);

    public string Venue => "binance";
    public MarketDataSessionPolicy SessionPolicy => MarketDataSessionPolicy.Concurrent;

    internal static TradeEvent ToTradeEvent(string instrument, BinanceAggTrade dto, TickScale scale)
    {
        var price = decimal.Parse(dto.Price,    CultureInfo.InvariantCulture);
        var qty   = decimal.Parse(dto.Quantity, CultureInfo.InvariantCulture);
        var side  = dto.IsBuyerMaker ? AggressorSide.Sell : AggressorSide.Buy;
        var tick  = new TradeTick(dto.EventTimeMs, scale.ScalePrice(price), scale.ScaleQty(qty), dto.AggId, side);
        return new TradeEvent(instrument, tick);
    }

    public async IAsyncEnumerable<IMarketEvent> Stream(
        IReadOnlyList<string> instruments,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = Channel.CreateBounded<IMarketEvent>(
            new BoundedChannelOptions(options.IngestChannelCapacity) { SingleReader = true });

        await using var ws = new BinanceWebSocketManager(
            options.MarketStreamUrl, options.ReconnectDelay, options.MaxReconnectAttempts, logger);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ws.Start(linkedCts);

        foreach (var symbol in instruments)
        {
            var scale = TickScaleFor(symbol);
            // Fire-and-forget: the WS read loop runs in background; TryWrite drops on overflow
            // (bounded channel) rather than blocking the callback thread.
            _ = ws.SubscribeAggTrade(symbol, dto => channel.Writer.TryWrite(ToTradeEvent(symbol, dto, scale)));
        }

        await foreach (var ev in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return ev;
    }

    public (sbyte PriceScaleExp, sbyte QtyScaleExp) InstrumentScale(string instrument)
    {
        var ts = TickScaleFor(instrument);
        return ((sbyte)ts.PriceExp, (sbyte)ts.QtyExp);
    }

    private TickScale TickScaleFor(string symbol) =>
        options.InstrumentScales.TryGetValue(symbol, out var scale) ? scale : DefaultTickScale;
}
