using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.LiveHost.Infrastructure.Live.Binance;
using AlgoTradeForge.LiveHost.Infrastructure.Live.DataPlane;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.DataPlane;

public class KlineVenueBarSourceTests
{
    private static BinanceKlineMessage Msg(
        long openTime, string o, string h, string l, string c, string v, bool closed) =>
        new()
        {
            EventType = "kline",
            EventTime = openTime + 1,
            Symbol = "BTCUSDT",
            Kline = new BinanceKlineData
            {
                OpenTime = openTime,
                CloseTime = openTime + 59_999,
                Symbol = "BTCUSDT",
                Interval = "1m",
                Open = o,
                High = h,
                Low = l,
                Close = c,
                Volume = v,
                IsClosed = closed,
            },
        };

    [Fact]
    public void MapKline_ScalesOhlcAndMapsVolume()
    {
        var scale = new ScaleContext(0.01m);
        var msg = Msg(1672515780000, "16500.50", "16550.00", "16490.25", "16540.75", "1234.567", closed: true);

        var bar = KlineVenueBarSource.MapKline(in msg, scale);

        Assert.Equal(1672515780000, bar.TimestampMs);
        Assert.Equal(scale.FromMarketPrice(16500.50m), bar.Open);
        Assert.Equal(scale.FromMarketPrice(16550.00m), bar.High);
        Assert.Equal(scale.FromMarketPrice(16490.25m), bar.Low);
        Assert.Equal(scale.FromMarketPrice(16540.75m), bar.Close);
        Assert.Equal(MoneyConvert.ToLong(1234.567m), bar.Volume);
    }

    [Fact]
    public async Task HandleMessage_NewOpenTime_EmitsOnlyBarStart_NotAddedToRecent()
    {
        var scale = new ScaleContext(0.01m);
        var emitted = new List<(Int64Bar Bar, bool IsStart)>();
        await using var source = new KlineVenueBarSource(
            "BTCUSDT", "1m", scale, (bar, isStart) => emitted.Add((bar, isStart)), recentCapacity: 8);

        source.HandleMessage(Msg(1000, "10", "12", "9", "11", "5", closed: false));

        var call = Assert.Single(emitted);
        Assert.True(call.IsStart);
        Assert.Equal(scale.FromMarketPrice(11m), call.Bar.Close);
        Assert.Empty(source.Recent); // bar-start bars do NOT enter Recent
    }

    [Fact]
    public async Task HandleMessage_StartThenIntermediateThenClose_EmitsOneStartAndOneComplete()
    {
        var scale = new ScaleContext(0.01m);
        var emitted = new List<(Int64Bar Bar, bool IsStart)>();
        await using var source = new KlineVenueBarSource(
            "BTCUSDT", "1m", scale, (bar, isStart) => emitted.Add((bar, isStart)), recentCapacity: 8);

        source.HandleMessage(Msg(1000, "10", "12", "9", "11", "5", closed: false));   // new bar -> start
        source.HandleMessage(Msg(1000, "10", "12", "8", "10", "6", closed: false));   // same open-time, not final -> nothing
        source.HandleMessage(Msg(1000, "10", "13", "9", "12", "7", closed: true));    // close -> complete

        Assert.Equal(2, emitted.Count);
        Assert.True(emitted[0].IsStart);
        Assert.False(emitted[1].IsStart);
        Assert.Equal(scale.FromMarketPrice(12m), emitted[1].Bar.Close);
        Assert.Single(source.Recent);
        Assert.Equal(emitted[1].Bar, source.Recent[0]);
    }

    [Fact]
    public async Task HandleMessage_NewOpenTime_AfterPriorBar_EmitsStartForNewBar()
    {
        var scale = new ScaleContext(0.01m);
        var emitted = new List<(Int64Bar Bar, bool IsStart)>();
        await using var source = new KlineVenueBarSource(
            "BTCUSDT", "1m", scale, (bar, isStart) => emitted.Add((bar, isStart)), recentCapacity: 8);

        source.HandleMessage(Msg(1000, "10", "12", "9", "11", "5", closed: false));  // bar A start
        source.HandleMessage(Msg(1000, "10", "13", "9", "12", "7", closed: true));   // bar A complete
        source.HandleMessage(Msg(60000, "12", "14", "12", "13", "3", closed: false)); // bar B start (new open-time)

        Assert.Equal(3, emitted.Count);
        Assert.True(emitted[0].IsStart);
        Assert.False(emitted[1].IsStart);
        Assert.True(emitted[2].IsStart);
        Assert.Equal(60000, emitted[2].Bar.TimestampMs);
        Assert.Single(source.Recent); // only the one completed bar
    }

    [Fact]
    public async Task Recent_EvictsOldestBeyondCapacity()
    {
        var scale = new ScaleContext(1m);
        await using var source = new KlineVenueBarSource("BTCUSDT", "1m", scale, (_, _) => { }, recentCapacity: 2);

        source.HandleMessage(Msg(1, "1", "1", "1", "1", "1", closed: true));
        source.HandleMessage(Msg(2, "2", "2", "2", "2", "2", closed: true));
        source.HandleMessage(Msg(3, "3", "3", "3", "3", "3", closed: true));

        Assert.Equal(2, source.Recent.Count);
        Assert.Equal(2, source.Recent[0].TimestampMs);
        Assert.Equal(3, source.Recent[1].TimestampMs);
    }

    [Fact]
    public async Task DisposeAsync_StopsEmitting()
    {
        var scale = new ScaleContext(1m);
        var bars = new List<Int64Bar>();
        var source = new KlineVenueBarSource("BTCUSDT", "1m", scale, (bar, _) => bars.Add(bar), recentCapacity: 8);

        await source.DisposeAsync();
        source.HandleMessage(Msg(1, "1", "1", "1", "1", "1", closed: true));

        Assert.Empty(bars);
    }
}
