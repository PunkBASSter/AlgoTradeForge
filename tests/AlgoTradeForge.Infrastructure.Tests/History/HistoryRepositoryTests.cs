using AlgoTradeForge.Application.CandleIngestion;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Infrastructure.History;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.Infrastructure.Tests.History;

public class HistoryRepositoryTests
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);
    private static readonly Asset BtcUsdt = Asset.Crypto("BTCUSDT", "Binance", 2);

    private readonly IInt64BarLoader _loader;
    private readonly HistoryRepository _repo;

    public HistoryRepositoryTests()
    {
        _loader = Substitute.For<IInt64BarLoader>();
        var options = Options.Create(new CandleStorageOptions { DataRoot = "/data" });
        _repo = new HistoryRepository(_loader, options);
    }

    private TimeSeries<Int64Bar> MakeMinuteSeries(int count)
    {
        var series = new TimeSeries<Int64Bar>(Start, OneMinute);
        for (var i = 0; i < count; i++)
            series.Add(new Int64Bar(100 + i, 200 + i, 50 + i, 150 + i, 1000));
        return series;
    }

    [Fact]
    public void Load_SameTimeframe_ReturnsRawData()
    {
        var sub = new DataSubscription(BtcUsdt, OneMinute);
        var raw = MakeMinuteSeries(10);
        _loader.Load("/data", "Binance", "BTCUSDT", 2,
            Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), OneMinute).Returns(raw);

        var result = _repo.Load(sub, new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31));

        Assert.Equal(10, result.Count);
        Assert.Equal(OneMinute, result.Step);
    }

    [Fact]
    public void Load_HigherTimeframe_Resamples()
    {
        var sub = new DataSubscription(BtcUsdt, TimeSpan.FromMinutes(5));
        var raw = MakeMinuteSeries(10);
        _loader.Load("/data", "Binance", "BTCUSDT", 2,
            Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), OneMinute).Returns(raw);

        var result = _repo.Load(sub, new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31));

        Assert.Equal(2, result.Count);
        Assert.Equal(TimeSpan.FromMinutes(5), result.Step);
    }

    [Fact]
    public void Load_LowerTimeframe_Throws()
    {
        var sub = new DataSubscription(BtcUsdt, TimeSpan.FromSeconds(30));

        Assert.Throws<ArgumentException>(() =>
            _repo.Load(sub, new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31)));
    }

    [Fact]
    public void Load_NoExchange_Throws()
    {
        var noExchangeAsset = Asset.Equity("TEST");
        var sub = new DataSubscription(noExchangeAsset, OneMinute);

        Assert.Throws<InvalidOperationException>(() =>
            _repo.Load(sub, new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31)));
    }

    [Fact]
    public void Load_EmptyData_ReturnsEmptySeries()
    {
        var sub = new DataSubscription(BtcUsdt, OneMinute);
        var raw = new TimeSeries<Int64Bar>(Start, OneMinute);
        _loader.Load("/data", "Binance", "BTCUSDT", 2,
            Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), OneMinute).Returns(raw);

        var result = _repo.Load(sub, new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31));

        Assert.Empty(result);
    }

    [Fact]
    public void Load_ResampledOhlcv_IsCorrect()
    {
        var sub = new DataSubscription(BtcUsdt, TimeSpan.FromMinutes(5));
        var series = new TimeSeries<Int64Bar>(Start, OneMinute);
        series.Add(new Int64Bar(100, 110, 90, 105, 1000));
        series.Add(new Int64Bar(105, 115, 95, 108, 2000));
        series.Add(new Int64Bar(108, 120, 85, 112, 1500));
        series.Add(new Int64Bar(112, 118, 92, 110, 1800));
        series.Add(new Int64Bar(110, 125, 88, 115, 2200));
        _loader.Load("/data", "Binance", "BTCUSDT", 2,
            Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), OneMinute).Returns(series);

        var result = _repo.Load(sub, new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31));

        Assert.Single(result);
        Assert.Equal(100, result[0].Open);
        Assert.Equal(125, result[0].High);
        Assert.Equal(85, result[0].Low);
        Assert.Equal(115, result[0].Close);
        Assert.Equal(8500, result[0].Volume);
    }
}
