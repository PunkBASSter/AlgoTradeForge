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

public sealed class KlinesArchiveMaterializerTests : IDisposable
{
    private const string KlineCsv =
        "1709251200000,50000.1,50100.2,49900.3,50050.4,12.5,1709254799999,625631.2,1500,6.25,312815.6,0\n" +
        "1709254800000,50050.4,50200.0,50000.0,50150.0,10.0,1709258399999,501500.0,1200,5.0,250750.0,0\n";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"atf-klm-{Guid.NewGuid():N}");
    private readonly IBinanceArchiveClient _archive = Substitute.For<IBinanceArchiveClient>();
    private readonly ISchemaManager _schema = Substitute.For<ISchemaManager>();
    private readonly IFeedStatusStore _statusStore = Substitute.For<IFeedStatusStore>();

    public KlinesArchiveMaterializerTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static AssetCollectionConfig SpotConfig(int decimalDigits = 2) => new()
    {
        Symbol = "BTCUSDT",
        Type = AssetTypes.Spot,
        DecimalDigits = decimalDigits
    };

    private static AssetCollectionConfig FuturesConfig(int decimalDigits = 2) => new()
    {
        Symbol = "BTCUSDT",
        Type = AssetTypes.Perpetual,
        DecimalDigits = decimalDigits
    };

    private static FeedCollectionConfig FeedConfig(string interval = "1h") => new()
    {
        Name = FeedNames.Candles,
        Interval = interval
    };

    private static Stream CsvStream(string csv) => new MemoryStream(Encoding.UTF8.GetBytes(csv));

    private KlinesArchiveMaterializer CandlesMaterializer() => new(
        FeedNames.Candles, "klines", supportsSpot: true,
        _archive, new PartitionFileWriter(), _schema, _statusStore,
        NullLogger<KlinesArchiveMaterializer>.Instance);

    private KlinesArchiveMaterializer MarkPriceMaterializer() => new(
        FeedNames.MarkPrice, "markPriceKlines", supportsSpot: false,
        _archive, new PartitionFileWriter(), _schema, _statusStore,
        NullLogger<KlinesArchiveMaterializer>.Instance);

    [Fact]
    public async Task MaterializeMonth_Candles_WritesScaledPartition()
    {
        _archive.DownloadMonthly("spot", "klines", "BTCUSDT", "1h", 2024, 3, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream?>(CsvStream(KlineCsv)));

        var result = await CandlesMaterializer().MaterializeMonth(
            SpotConfig(2), FeedConfig(), _dir, 2024, 3, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.RowsWritten);
        Assert.True(result.AvailableAtSource);

        var path = Path.Combine(_dir, "candles", "2024-03_1h.csv");
        Assert.True(File.Exists(path));
        var lines = await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal("ts,o,h,l,c,vol", lines[0]);
        Assert.Equal("1709251200000,5000010,5010020,4990030,5005040,1250", lines[1]);
    }

    [Fact]
    public async Task MaterializeMonth_Futures_WritesCandleExtWithProxy()
    {
        _archive.DownloadMonthly("futures/um", "klines", "BTCUSDT", "1h", 2024, 3, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream?>(CsvStream(KlineCsv)));

        await CandlesMaterializer().MaterializeMonth(
            FuturesConfig(2), FeedConfig(), _dir, 2024, 3, TestContext.Current.CancellationToken);

        var path = Path.Combine(_dir, "candle-ext", "2024-03_1h.csv");
        Assert.True(File.Exists(path));
        var lines = await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal("ts,quote_vol,trade_count,taker_buy_vol,taker_buy_quote_vol,taker_buy_trade_count", lines[0]);
        Assert.Equal("1709251200000,625631.2,1500,6.25,312815.6,750", lines[1]);
    }

    [Fact]
    public async Task MaterializeMonth_Spot_SkipsCandleExt()
    {
        _archive.DownloadMonthly("spot", "klines", "BTCUSDT", "1h", 2024, 3, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream?>(CsvStream(KlineCsv)));

        await CandlesMaterializer().MaterializeMonth(
            SpotConfig(), FeedConfig(), _dir, 2024, 3, TestContext.Current.CancellationToken);

        Assert.False(Directory.Exists(Path.Combine(_dir, "candle-ext")));
    }

    [Fact]
    public async Task MaterializeMonth_MicrosecondTimestamps_Normalized()
    {
        // Spot 2025+ format: timestamps in microseconds (×1000)
        const string microCsv =
            "1709251200000000,50000.1,50100.2,49900.3,50050.4,12.5,1709254799999000,625631.2,1500,6.25,312815.6,0\n" +
            "1709254800000000,50050.4,50200.0,50000.0,50150.0,10.0,1709258399999000,501500.0,1200,5.0,250750.0,0\n";

        _archive.DownloadMonthly("spot", "klines", "BTCUSDT", "1h", 2024, 3, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream?>(CsvStream(microCsv)));

        var result = await CandlesMaterializer().MaterializeMonth(
            SpotConfig(2), FeedConfig(), _dir, 2024, 3, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.RowsWritten);
        var lines = await File.ReadAllLinesAsync(
            Path.Combine(_dir, "candles", "2024-03_1h.csv"), TestContext.Current.CancellationToken);
        Assert.Equal("1709251200000,5000010,5010020,4990030,5005040,1250", lines[1]);
    }

    [Fact]
    public async Task MaterializeMonth_MonthlyMissing_AssemblesFromDailies()
    {
        _archive.DownloadMonthly(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream?>(null));

        // Only 2024-03-01 has data; all other days return null (NSubstitute default)
        _archive.DownloadDaily("spot", "klines", "BTCUSDT", "1h", new DateOnly(2024, 3, 1), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream?>(CsvStream(KlineCsv)));

        var result = await CandlesMaterializer().MaterializeMonth(
            SpotConfig(), FeedConfig(), _dir, 2024, 3, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.RowsWritten);
        Assert.True(result.AvailableAtSource);

        // All 31 days of March 2024 must be attempted
        await _archive.Received(31).DownloadDaily(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
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

        var result = await CandlesMaterializer().MaterializeMonth(
            SpotConfig(), FeedConfig(), _dir, 2024, 3, TestContext.Current.CancellationToken);

        Assert.False(result.AvailableAtSource);
        Assert.Equal(0, result.RowsWritten);
        Assert.False(Directory.Exists(Path.Combine(_dir, "candles")));
    }

    [Fact]
    public async Task MaterializeMonth_ArchivePresentButNoInRangeRows_ReportsAvailable()
    {
        // Rows stamped 2024-02-29 while materializing 2024-03 — file present but nothing in-range
        const string outOfRangeCsv =
            "1709164800000,50000.1,50100.2,49900.3,50050.4,12.5,1709168399999,625631.2,1500,6.25,312815.6,0\n";

        _archive.DownloadMonthly("spot", "klines", "BTCUSDT", "1h", 2024, 3, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream?>(CsvStream(outOfRangeCsv)));

        var result = await CandlesMaterializer().MaterializeMonth(
            SpotConfig(), FeedConfig(), _dir, 2024, 3, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.RowsWritten);
        Assert.True(result.AvailableAtSource);
        Assert.False(Directory.Exists(Path.Combine(_dir, "candles")));
    }

    [Fact]
    public async Task MaterializeMonth_MarkPrice_WritesOhlcDoubles()
    {
        _archive.DownloadMonthly("futures/um", "markPriceKlines", "BTCUSDT", "1h", 2024, 3, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream?>(CsvStream(KlineCsv)));

        var result = await MarkPriceMaterializer().MaterializeMonth(
            FuturesConfig(2), FeedConfig(), _dir, 2024, 3, TestContext.Current.CancellationToken);

        Assert.True(result.AvailableAtSource);
        var path = Path.Combine(_dir, "mark-price", "2024-03_1h.csv");
        Assert.True(File.Exists(path));
        var lines = await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal("ts,o,h,l,c", lines[0]);
        Assert.Equal("1709251200000,50000.1,50100.2,49900.3,50050.4", lines[1]);
    }

    [Fact]
    public async Task MaterializeMonth_UpdatesFeedStatus()
    {
        _archive.DownloadMonthly("spot", "klines", "BTCUSDT", "1h", 2024, 3, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream?>(CsvStream(KlineCsv)));

        await CandlesMaterializer().MaterializeMonth(
            SpotConfig(), FeedConfig(), _dir, 2024, 3, TestContext.Current.CancellationToken);

        await _statusStore.Received(1).Save(
            _dir, FeedNames.Candles, "1h",
            Arg.Is<FeedStatus>(s => s.RecordCount == 2 && s.LastTimestamp == 1709254800000),
            Arg.Any<CancellationToken>());
    }
}
