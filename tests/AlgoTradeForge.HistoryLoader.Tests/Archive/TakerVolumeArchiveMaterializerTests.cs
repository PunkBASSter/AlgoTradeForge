using System.Globalization;
using System.Text;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Archive;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Infrastructure.Archive;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Archive;

public sealed class TakerVolumeArchiveMaterializerTests : IDisposable
{
    // row: openTime,o,h,l,c,vol,closeTime,quote_vol,count,taker_buy_vol,taker_buy_quote_vol,ignore
    private const string KlineCsv =
        "1709251200000,50000,50100,49900,50050,12.5,1709254799999,625000,1500,6.25,375000,0\n";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"atf-tvm-{Guid.NewGuid():N}");
    private readonly IBinanceArchiveClient _archive = Substitute.For<IBinanceArchiveClient>();
    private readonly ISchemaManager _schema = Substitute.For<ISchemaManager>();
    private readonly IFeedStatusStore _statusStore = Substitute.For<IFeedStatusStore>();

    public TakerVolumeArchiveMaterializerTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static AssetCollectionConfig FuturesConfig() => new()
    {
        Symbol = "BTCUSDT",
        Type = AssetTypes.Perpetual,
        DecimalDigits = 2
    };

    private static AssetCollectionConfig SpotConfig() => new()
    {
        Symbol = "BTCUSDT",
        Type = AssetTypes.Spot,
        DecimalDigits = 2
    };

    private static FeedCollectionConfig FeedConfig(string interval = "15m") => new()
    {
        Name = FeedNames.TakerVolume,
        Interval = interval
    };

    private static Stream CsvStream(string csv) => new MemoryStream(Encoding.UTF8.GetBytes(csv));

    private TakerVolumeArchiveMaterializer CreateSut() => new(
        _archive, new PartitionFileWriter(), _schema, _statusStore,
        NullLogger<TakerVolumeArchiveMaterializer>.Instance);

    [Fact]
    public async Task MaterializeMonth_DerivesTakerVolumeColumns()
    {
        // buy=375000; sell=625000-375000=250000; ratio=375000/250000=1.5
        _archive.DownloadMonthly("futures/um", "klines", "BTCUSDT", "15m", 2024, 3, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream?>(CsvStream(KlineCsv)));

        var result = await CreateSut().MaterializeMonth(
            FuturesConfig(), FeedConfig("15m"), _dir, 2024, 3, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.RowsWritten);
        Assert.True(result.AvailableAtSource);

        var path = Path.Combine(_dir, "taker-volume", "2024-03_15m.csv");
        Assert.True(File.Exists(path));
        var lines = await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal("ts,buy_vol_usd,sell_vol_usd,ratio", lines[0]);
        Assert.Equal(
            $"1709251200000,{375000d.ToString(CultureInfo.InvariantCulture)},{250000d.ToString(CultureInfo.InvariantCulture)},{1.5d.ToString(CultureInfo.InvariantCulture)}",
            lines[1]);
    }

    [Fact]
    public async Task MaterializeMonth_ZeroSellVolume_RatioZero()
    {
        // quote_vol == taker_buy_quote_vol → sell_vol_usd = 0 → ratio = 0
        const string zeroCsv =
            "1709251200000,50000,50100,49900,50050,12.5,1709254799999,625000,1500,6.25,625000,0\n";

        _archive.DownloadMonthly("futures/um", "klines", "BTCUSDT", "15m", 2024, 3, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream?>(CsvStream(zeroCsv)));

        await CreateSut().MaterializeMonth(
            FuturesConfig(), FeedConfig("15m"), _dir, 2024, 3, TestContext.Current.CancellationToken);

        var path = Path.Combine(_dir, "taker-volume", "2024-03_15m.csv");
        var lines = await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken);
        // sell_vol_usd = 625000 - 625000 = 0 → ratio = 0
        Assert.Equal(
            $"1709251200000,{625000d.ToString(CultureInfo.InvariantCulture)},{0d.ToString(CultureInfo.InvariantCulture)},{0d.ToString(CultureInfo.InvariantCulture)}",
            lines[1]);
    }

    [Fact]
    public void MaterializeMonth_RejectsSpot()
    {
        var sut = CreateSut();
        Assert.False(sut.Supports(AssetTypes.Spot));
        Assert.True(sut.Supports(AssetTypes.Perpetual));
        Assert.True(sut.Supports(AssetTypes.Future));
    }

    [Fact]
    public async Task MaterializeMonth_NothingAtSource_ReportsUnavailable()
    {
        _archive.DownloadMonthly(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream?>(null));
        _archive.DownloadDaily(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream?>(null));

        var result = await CreateSut().MaterializeMonth(
            FuturesConfig(), FeedConfig("15m"), _dir, 2024, 3, TestContext.Current.CancellationToken);

        Assert.False(result.AvailableAtSource);
        Assert.Equal(0, result.RowsWritten);
        Assert.False(Directory.Exists(Path.Combine(_dir, "taker-volume")));
    }
}
