using AlgoTradeForge.Application.CandleIngestion;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Infrastructure.History;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.Infrastructure.Tests.History;

public class CsvDataSourceTests
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = new(2024, 1, 31, 23, 59, 0, TimeSpan.Zero);
    private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);

    private static readonly Asset TestAsset = Asset.Crypto("BTCUSDT", "Binance", 2);

    private readonly IInt64BarLoader _loader = Substitute.For<IInt64BarLoader>();
    private readonly IOptions<CandleStorageOptions> _options =
        Options.Create(new CandleStorageOptions { DataRoot = "/data" });

    private CsvDataSource CreateSource() => new(_loader, _options);

    private TimeSeries<Int64Bar> MakeMinuteSeries(int count)
    {
        var series = new TimeSeries<Int64Bar>();
        var startMs = Start.ToUnixTimeMilliseconds();
        var stepMs = (long)OneMinute.TotalMilliseconds;
        for (var i = 0; i < count; i++)
            series.Add(new Int64Bar(startMs + i * stepMs, 100 + i, 200 + i, 50 + i, 150 + i, 1000));
        return series;
    }

    private void SetupLoader(TimeSeries<Int64Bar> series)
    {
        _loader.Load(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<int>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(),
            Arg.Any<TimeSpan>())
            .Returns(series);
    }

    [Fact]
    public void GetData_SameInterval_ReturnsRaw()
    {
        var raw = MakeMinuteSeries(10);
        SetupLoader(raw);
        var source = CreateSource();

        var query = new HistoryDataQuery
        {
            Asset = TestAsset,
            TimeFrame = OneMinute,
            StartTime = Start,
            EndTime = End
        };

        var result = source.GetData(query);

        Assert.Equal(10, result.Count);
    }

    [Fact]
    public void GetData_LargerInterval_ReturnsResampled()
    {
        var raw = MakeMinuteSeries(10);
        SetupLoader(raw);
        var source = CreateSource();

        var query = new HistoryDataQuery
        {
            Asset = TestAsset,
            TimeFrame = TimeSpan.FromMinutes(5),
            StartTime = Start,
            EndTime = End
        };

        var result = source.GetData(query);

        Assert.Equal(2, result.Count); // 10 bars / 5 = 2
    }

    [Fact]
    public void GetData_SmallerInterval_Throws()
    {
        var source = CreateSource();

        var query = new HistoryDataQuery
        {
            Asset = TestAsset,
            TimeFrame = TimeSpan.FromSeconds(30),
            StartTime = Start,
            EndTime = End
        };

        Assert.Throws<ArgumentException>(() => source.GetData(query));
    }

    [Fact]
    public void GetData_NullExchange_Throws()
    {
        var assetNoExchange = Asset.Equity("SPY");
        var source = CreateSource();

        var query = new HistoryDataQuery
        {
            Asset = assetNoExchange,
            TimeFrame = OneMinute,
            StartTime = Start,
            EndTime = End
        };

        Assert.Throws<InvalidOperationException>(() => source.GetData(query));
    }

    [Fact]
    public void GetData_NullStartTime_Throws()
    {
        var source = CreateSource();

        var query = new HistoryDataQuery
        {
            Asset = TestAsset,
            TimeFrame = OneMinute,
            EndTime = End
        };

        Assert.Throws<ArgumentException>(() => source.GetData(query));
    }

    [Fact]
    public void GetData_NullEndTime_Throws()
    {
        var source = CreateSource();

        var query = new HistoryDataQuery
        {
            Asset = TestAsset,
            TimeFrame = OneMinute,
            StartTime = Start
        };

        Assert.Throws<ArgumentException>(() => source.GetData(query));
    }
}
