using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.LiveHost.Application.Live.DataPlane;
using AlgoTradeForge.LiveHost.Infrastructure.Live.Binance;
using System.Globalization;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.DataPlane;

/// <summary>
/// Venue-published TIME bar source: subscribes the Binance kline WebSocket and emits an
/// <see cref="Int64Bar"/> on each bar-open (OnBarStart, rare) and on each <em>closed</em> kline
/// (OnBarComplete). Not tick-driven — the venue aggregates.
/// </summary>
public sealed class KlineVenueBarSource : IBarSource, IAsyncDisposable
{
    private readonly BinanceWebSocketManager? _ws;
    private readonly string _symbol;
    private readonly string _interval;
    private readonly ScaleContext _scale;
    private readonly Action<Int64Bar, bool> _onBar;
    private readonly int _recentCapacity;
    private readonly Queue<Int64Bar> _recent;
    private readonly Lock _gate = new();
    private long? _lastOpenTimeMs; // last open-time seen; a change means a new bar opened (emit OnBarStart once)
    private volatile bool _disposed;

    public KlineVenueBarSource(
        BinanceWebSocketManager ws, string symbol, string interval,
        ScaleContext scale, Action<Int64Bar, bool> onBar, int recentCapacity = 256)
        : this(symbol, interval, scale, onBar, recentCapacity)
    {
        ArgumentNullException.ThrowIfNull(ws);
        _ws = ws;
    }

    internal KlineVenueBarSource(
        string symbol, string interval, ScaleContext scale,
        Action<Int64Bar, bool> onBar, int recentCapacity)
    {
        ArgumentException.ThrowIfNullOrEmpty(symbol);
        ArgumentException.ThrowIfNullOrEmpty(interval);
        ArgumentNullException.ThrowIfNull(onBar);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(recentCapacity);

        _symbol = symbol;
        _interval = interval;
        _scale = scale;
        _onBar = onBar;
        _recentCapacity = recentCapacity;
        _recent = new Queue<Int64Bar>(recentCapacity);
    }

    public IReadOnlyList<Int64Bar> Recent
    {
        get { lock (_gate) return _recent.ToArray(); }
    }

    /// <summary>Subscribe the kline WS. The returned task completes once the stream is connected.</summary>
    public Task Start() =>
        _ws is null
            ? throw new InvalidOperationException("KlineVenueBarSource was constructed without a WebSocket manager.")
            : _ws.SubscribeKline(_symbol, _interval, HandleMessage);

    internal void HandleMessage(BinanceKlineMessage msg)
    {
        if (_disposed)
            return;

        var openTime = msg.Kline.OpenTime;

        // A new open-time means a bar just opened: emit OnBarStart exactly once for the forming bar.
        // Bar-start bars are NOT added to Recent (Recent holds completed bars only).
        if (_lastOpenTimeMs != openTime)
        {
            _lastOpenTimeMs = openTime;
            _onBar(MapKline(in msg, _scale), true); // isStart
        }

        if (!msg.Kline.IsClosed)
            return;

        var bar = MapKline(in msg, _scale);
        lock (_gate)
        {
            if (_disposed) return;
            if (_recent.Count >= _recentCapacity) _recent.Dequeue();
            _recent.Enqueue(bar);
        }
        _onBar(bar, false); // isStart
    }

    internal static Int64Bar MapKline(in BinanceKlineMessage msg, ScaleContext scale)
    {
        var k = msg.Kline;
        return new Int64Bar(
            k.OpenTime,
            scale.FromMarketPrice(decimal.Parse(k.Open, CultureInfo.InvariantCulture)),
            scale.FromMarketPrice(decimal.Parse(k.High, CultureInfo.InvariantCulture)),
            scale.FromMarketPrice(decimal.Parse(k.Low, CultureInfo.InvariantCulture)),
            scale.FromMarketPrice(decimal.Parse(k.Close, CultureInfo.InvariantCulture)),
            MoneyConvert.ToLong(decimal.Parse(k.Volume, CultureInfo.InvariantCulture))); // Volume: not monetary, rounding is correct for fractional quantities
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }
}
