using System.Text;
using AlgoTradeForge.Application.CandleIngestion;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Subscriptions;
using AlgoTradeForge.Infrastructure.History;
using AlgoTradeForge.Storage;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.Infrastructure.Tests.History;

public class HistoryRepositoryTests
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeFrame OneMinute = new(TimeSpan.FromMinutes(1));
    private static readonly CryptoAsset BtcUsdt = CryptoAsset.Create("BTCUSDT", "Binance", 2);

    private readonly IInt64BarLoader _loader;
    private readonly IFileStorage _storage;
    private readonly HistoryRepository _repo;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public HistoryRepositoryTests()
    {
        _loader = Substitute.For<IInt64BarLoader>();
        _storage = Substitute.For<IFileStorage>();
        // Default: no feeds.json → the source-resample path (existing crypto behavior).
        _storage.Exists(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(false));
        var options = Options.Create(new CandleStorageOptions { DataRoot = "/data" });
        _repo = new HistoryRepository(_loader, _storage, options);
    }

    // Configures _storage to return a feeds.json declaring the given candle intervals.
    private void WithCandleIntervals(params string[] intervals)
    {
        var json = "{\"candles\":{\"scaleFactor\":100,\"intervals\":[" +
            string.Join(",", intervals.Select(i => $"\"{i}\"")) + "]}}";
        _storage.Exists(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));
        _storage.OpenRead(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(json))));
    }

    private TimeSeries<Int64Bar> MakeMinuteSeries(int count)
    {
        var series = new TimeSeries<Int64Bar>();
        var startMs = Start.ToUnixTimeMilliseconds();
        var stepMs = (long)OneMinute.Duration.TotalMilliseconds;
        for (var i = 0; i < count; i++)
            series.Add(new Int64Bar(startMs + i * stepMs, 100 + i, 200 + i, 50 + i, 150 + i, 1000));
        return series;
    }

    [Fact]
    public async Task Load_SameTimeframe_ReturnsRawData()
    {
        var sub = TestSubs.Of(BtcUsdt, OneMinute);
        var raw = MakeMinuteSeries(10);
        _loader.Load(Arg.Any<DataFeedDescriptor>(),
            Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()).Returns(raw);

        var result = await _repo.Load(sub, new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31), ct: Ct);

        Assert.Equal(10, result.Count);
    }

    [Fact]
    public async Task Load_HigherTimeframe_Resamples()
    {
        var sub = TestSubs.Of(BtcUsdt, new TimeFrame(TimeSpan.FromMinutes(5)));
        var raw = MakeMinuteSeries(10);
        _loader.Load(Arg.Any<DataFeedDescriptor>(),
            Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()).Returns(raw);

        var result = await _repo.Load(sub, new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31), ct: Ct);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task Load_LowerTimeframe_Throws()
    {
        var sub = TestSubs.Of(BtcUsdt, new TimeFrame(TimeSpan.FromSeconds(30)));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _repo.Load(sub, new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31), ct: Ct));
    }

    [Fact]
    public async Task Load_EmptyData_ReturnsEmptySeries()
    {
        var sub = TestSubs.Of(BtcUsdt, OneMinute);
        var raw = new TimeSeries<Int64Bar>();
        _loader.Load(Arg.Any<DataFeedDescriptor>(),
            Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()).Returns(raw);

        var result = await _repo.Load(sub, new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31), ct: Ct);

        Assert.Empty(result);
    }

    [Fact]
    public async Task Load_ResampledOhlcv_IsCorrect()
    {
        var sub = TestSubs.Of(BtcUsdt, new TimeFrame(TimeSpan.FromMinutes(5)));
        var series = new TimeSeries<Int64Bar>();
        var ms = Start.ToUnixTimeMilliseconds();
        var step = (long)OneMinute.Duration.TotalMilliseconds;
        series.Add(new Int64Bar(ms, 100, 110, 90, 105, 1000));
        series.Add(new Int64Bar(ms + step, 105, 115, 95, 108, 2000));
        series.Add(new Int64Bar(ms + 2 * step, 108, 120, 85, 112, 1500));
        series.Add(new Int64Bar(ms + 3 * step, 112, 118, 92, 110, 1800));
        series.Add(new Int64Bar(ms + 4 * step, 110, 125, 88, 115, 2200));
        _loader.Load(Arg.Any<DataFeedDescriptor>(),
            Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()).Returns(series);

        var result = await _repo.Load(sub, new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31), ct: Ct);

        Assert.Single(result);
        Assert.Equal(100, result[0].Open);
        Assert.Equal(125, result[0].High);
        Assert.Equal(85, result[0].Low);
        Assert.Equal(115, result[0].Close);
        Assert.Equal(8500, result[0].Volume);
    }

    [Fact]
    public async Task LoadFeedSubscription_TimeBar_BuildsTimeBarDescriptor()
    {
        var sub = new TimeBarSubscription("BTCUSDT", "Binance", DataFeedRole.Primary, OneMinute);
        DataFeedDescriptor? captured = null;
        _loader.Load(Arg.Any<DataFeedDescriptor>(),
                Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                captured = call.Arg<DataFeedDescriptor>();
                return MakeMinuteSeries(3);
            });

        var result = await _repo.Load(BtcUsdt, sub, new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31), ct: Ct);

        Assert.NotNull(captured);
        Assert.Equal(DataFeedKind.TimeBar, captured!.Value.Kind);
        Assert.Equal("BTCUSDT", captured.Value.Asset);
        Assert.Equal("Binance", captured.Value.Exchange);
        Assert.Equal("/data", captured.Value.DataRoot);
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task LoadFeedSubscription_AltBar_BuildsAltBarDescriptorWithFeedId()
    {
        var sub = new AltBarSubscription("BTCUSDT", "Binance", DataFeedRole.Primary, "EqV_1m_1000");
        DataFeedDescriptor? captured = null;
        _loader.Load(Arg.Any<DataFeedDescriptor>(),
                Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                captured = call.Arg<DataFeedDescriptor>();
                return new TimeSeries<Int64Bar>();
            });

        await _repo.Load(BtcUsdt, sub, new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 30), ct: Ct);

        Assert.NotNull(captured);
        Assert.Equal(DataFeedKind.AltBar, captured!.Value.Kind);
        Assert.Equal("EqV_1m_1000", captured.Value.FeedId);
        Assert.Equal("BTCUSDT", captured.Value.Asset);
    }

    [Fact]
    public async Task LoadFeedSubscription_AltBar_DoesNotResample()
    {
        var sub = new AltBarSubscription("BTCUSDT", "Binance", DataFeedRole.Primary, "EqV_1m_1000");
        var raw = new TimeSeries<Int64Bar>();
        raw.Add(new Int64Bar(100, 1, 1, 1, 1, 100));
        raw.Add(new Int64Bar(200, 2, 2, 2, 2, 200));
        raw.Add(new Int64Bar(300, 3, 3, 3, 3, 300));
        _loader.Load(Arg.Any<DataFeedDescriptor>(),
            Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()).Returns(raw);

        var result = await _repo.Load(BtcUsdt, sub, new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 30), ct: Ct);

        Assert.Equal(3, result.Count);
        Assert.Equal(100, result[0].TimestampMs);
        Assert.Equal(200, result[1].TimestampMs);
        Assert.Equal(300, result[2].TimestampMs);
    }

    [Fact]
    public async Task LoadFeedSubscription_Tick_BuildsTickDescriptorWithTicksFeedId()
    {
        var sub = new TickSubscription("BTCUSDT", "Binance", DataFeedRole.Primary);
        DataFeedDescriptor? captured = null;
        _loader.Load(Arg.Any<DataFeedDescriptor>(),
                Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                captured = call.Arg<DataFeedDescriptor>();
                return new TimeSeries<Int64Bar>();
            });

        await _repo.Load(BtcUsdt, sub, new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 30), ct: Ct);

        Assert.NotNull(captured);
        Assert.Equal(DataFeedKind.Tick, captured!.Value.Kind);
        Assert.Equal("ticks", captured.Value.FeedId);
    }

    [Fact]
    public async Task LoadFeedSubscription_Side_ThrowsArgumentException()
    {
        var sub = new SideFeedSubscription("BTCUSDT", "Binance", DataFeedRole.Side, "funding-rate");

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            _repo.Load(BtcUsdt, sub, new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 30), ct: Ct));

        Assert.Contains("Side feeds", ex.Message);
        Assert.Contains("IFeedContext", ex.Message);
    }

    [Fact]
    public async Task LoadFeedSubscription_TimeBar_ResamplesAtHigherTimeFrame()
    {
        var sub = new TimeBarSubscription("BTCUSDT", "Binance", DataFeedRole.Primary,
            new TimeFrame(TimeSpan.FromMinutes(5)));
        var raw = MakeMinuteSeries(10);
        _loader.Load(Arg.Any<DataFeedDescriptor>(),
            Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()).Returns(raw);

        var result = await _repo.Load(BtcUsdt, sub, new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31), ct: Ct);

        Assert.Equal(2, result.Count);
    }

    // Equity archive carries 5m/1d but no 1m source: the requested timeframe must be loaded
    // from its own native partitions (FeedId='1d'), not the absent 1m source.
    [Fact]
    public async Task LoadTimeBar_WhenSourceIntervalAbsent_LoadsRequestedTimeframeNatively()
    {
        WithCandleIntervals("5m", "1d");
        var sub = new TimeBarSubscription("AAPL", "NASDAQ", DataFeedRole.Primary, TimeFrame.Parse("1d"));
        var equity = new EquityAsset { Name = "AAPL", Exchange = "NASDAQ" };
        DataFeedDescriptor? captured = null;
        _loader.Load(Arg.Any<DataFeedDescriptor>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(call => { captured = call.Arg<DataFeedDescriptor>(); return MakeMinuteSeries(3); });

        var result = await _repo.Load(equity, sub, new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31), ct: Ct);

        Assert.NotNull(captured);
        Assert.Equal("1d", captured!.Value.FeedId);       // native, not the 1m source
        Assert.Equal(3, result.Count);                     // returned raw (no resample)
    }

    // When the 1m source IS present (crypto), keep loading it and resampling — unchanged behavior.
    [Fact]
    public async Task LoadTimeBar_WhenSourceIntervalPresent_LoadsSourceAndResamples()
    {
        WithCandleIntervals("1m", "1d");
        var sub = new TimeBarSubscription("BTCUSDT", "Binance", DataFeedRole.Primary, TimeFrame.Parse("1d"));
        DataFeedDescriptor? captured = null;
        _loader.Load(Arg.Any<DataFeedDescriptor>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(call => { captured = call.Arg<DataFeedDescriptor>(); return MakeMinuteSeries(10); });

        await _repo.Load(BtcUsdt, sub, new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31), ct: Ct);

        Assert.NotNull(captured);
        Assert.Equal("1m", captured!.Value.FeedId);       // source, then resampled to 1d
    }

    // Non-native equity timeframe: resample from the largest available interval that cleanly
    // divides the request (1h from 5m; 1m source is absent).
    [Fact]
    public async Task LoadTimeBar_NonNativeTimeframe_ResamplesFromLargestDivisor()
    {
        WithCandleIntervals("5m", "1d");
        var sub = new TimeBarSubscription("AAPL", "NASDAQ", DataFeedRole.Primary, TimeFrame.Parse("1h"));
        var equity = new EquityAsset { Name = "AAPL", Exchange = "NASDAQ" };
        DataFeedDescriptor? captured = null;
        _loader.Load(Arg.Any<DataFeedDescriptor>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(call => { captured = call.Arg<DataFeedDescriptor>(); return MakeMinuteSeries(120); });

        await _repo.Load(equity, sub, new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31), ct: Ct);

        Assert.NotNull(captured);
        Assert.Equal("5m", captured!.Value.FeedId);   // resampled from 5m (divides 1h), not 1d
    }

    // No feed on disk can produce the request (5m from a 1d-only archive) → clear error.
    [Fact]
    public async Task LoadTimeBar_NoFeedCanProduceTimeframe_Throws()
    {
        WithCandleIntervals("1d");
        var sub = new TimeBarSubscription("AAPL", "NASDAQ", DataFeedRole.Primary, TimeFrame.Parse("5m"));
        var equity = new EquityAsset { Name = "AAPL", Exchange = "NASDAQ" };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            _repo.Load(equity, sub, new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31), ct: Ct));
        Assert.Contains("No feed on disk", ex.Message);
    }

    [Fact]
    public async Task LoadFeedSubscription_PerpetualAsset_AppendsPerpSuffix()
    {
        var perp = CryptoPerpetualAsset.Create("BTCUSDT", "Binance", 2);
        var sub = new AltBarSubscription("BTCUSDT", "Binance", DataFeedRole.Primary, "EqV_1m_1000");
        DataFeedDescriptor? captured = null;
        _loader.Load(Arg.Any<DataFeedDescriptor>(),
                Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                captured = call.Arg<DataFeedDescriptor>();
                return new TimeSeries<Int64Bar>();
            });

        await _repo.Load(perp, sub, new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 30), ct: Ct);

        Assert.NotNull(captured);
        Assert.Equal("BTCUSDT_perp", captured!.Value.Asset);
    }
}
