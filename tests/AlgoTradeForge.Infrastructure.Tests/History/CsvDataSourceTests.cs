using AlgoTradeForge.Application.CandleIngestion;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Infrastructure.History;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.Infrastructure.Tests.History;

public class CsvDataSourceTests
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = new(2024, 1, 31, 23, 59, 0, TimeSpan.Zero);
    private static readonly TimeFrame OneMinute = new(TimeSpan.FromMinutes(1));

    private static readonly CryptoAsset TestAsset = CryptoAsset.Create("BTCUSDT", "Binance", 2);

    private readonly IInt64BarLoader _loader = Substitute.For<IInt64BarLoader>();
    private readonly IOptions<CandleStorageOptions> _options =
        Options.Create(new CandleStorageOptions { DataRoot = "/data" });
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private CsvDataSource CreateSource() => new(_loader, _options);

    private TimeSeries<Int64Bar> MakeMinuteSeries(int count)
    {
        var series = new TimeSeries<Int64Bar>();
        var startMs = Start.ToUnixTimeMilliseconds();
        var stepMs = (long)OneMinute.Duration.TotalMilliseconds;
        for (var i = 0; i < count; i++)
            series.Add(new Int64Bar(startMs + i * stepMs, 100 + i, 200 + i, 50 + i, 150 + i, 1000));
        return series;
    }

    private void SetupLoader(TimeSeries<Int64Bar> series)
    {
        _loader.Load(
            Arg.Any<DataFeedDescriptor>(),
            Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(series);
    }

    [Fact]
    public async Task GetData_SameInterval_ReturnsRaw()
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

        var result = await source.GetData(query, ct: Ct);

        Assert.Equal(10, result.Count);
    }

    [Fact]
    public async Task GetData_LargerInterval_ReturnsResampled()
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

        var result = await source.GetData(query, ct: Ct);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task GetData_SmallerInterval_Throws()
    {
        var source = CreateSource();

        var query = new HistoryDataQuery
        {
            Asset = TestAsset,
            TimeFrame = TimeSpan.FromSeconds(30),
            StartTime = Start,
            EndTime = End
        };

        await Assert.ThrowsAsync<ArgumentException>(() => source.GetData(query, ct: Ct));
    }

    [Fact]
    public async Task GetData_NullStartTime_Throws()
    {
        var source = CreateSource();

        var query = new HistoryDataQuery
        {
            Asset = TestAsset,
            TimeFrame = OneMinute,
            EndTime = End
        };

        await Assert.ThrowsAsync<ArgumentException>(() => source.GetData(query, ct: Ct));
    }

    [Fact]
    public async Task GetData_NullEndTime_Throws()
    {
        var source = CreateSource();

        var query = new HistoryDataQuery
        {
            Asset = TestAsset,
            TimeFrame = OneMinute,
            StartTime = Start
        };

        await Assert.ThrowsAsync<ArgumentException>(() => source.GetData(query, ct: Ct));
    }
}
