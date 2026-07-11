using System.Text;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Archive;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Tests.TestData;
using AlgoTradeForge.HistoryLoader.Infrastructure.Archive;
using AlgoTradeForge.HistoryLoader.Infrastructure.State;
using AlgoTradeForge.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Archive;

public sealed class AggTradesArchiveMaterializerTests : IDisposable
{
    // futures/um aggTrades, 7 cols, header present. Two trades on 2024-03-01, one on 2024-03-02.
    private const string FuturesAgg =
        "agg_trade_id,price,quantity,first_trade_id,last_trade_id,transact_time,is_buyer_maker\n" +
        "100,50000.5,0.100,1,1,1709251200000,true\n" +   // 2024-03-01 00:00
        "101,50001.0,0.200,2,2,1709251260000,false\n" +  // 2024-03-01 00:01
        "102,50002.0,0.050,3,3,1709337600000,true\n";    // 2024-03-02 00:00

    // spot aggTrades, 8 cols, µs timestamps (2025+), trailing is_best_match.
    private const string SpotAggMicros =
        "1000,60000.00,0.010,10,10,1735689600000000,false,true\n"; // 2025-01-01 00:00 µs

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"atf-agg-{Guid.NewGuid():N}");
    private readonly IBinanceArchiveClient _archive = Substitute.For<IBinanceArchiveClient>();
    private readonly ISchemaManager _schema = Substitute.For<ISchemaManager>();
    private readonly IFeedStatusStore _statusStore = Substitute.For<IFeedStatusStore>();

    public AggTradesArchiveMaterializerTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CollectionAsset SpotConfig(int decimalDigits = 2) =>
        CollectionAssets.Spot("BTCUSDT", decimalDigits);

    private static CollectionAsset FuturesConfig(int decimalDigits = 2) =>
        CollectionAssets.Perp("BTCUSDT", decimalDigits);

    private static CollectionFeed FeedConfig() => CollectionAssets.Feed(FeedNames.Ticks, "");

    private static Stream CsvStream(string csv) => new MemoryStream(Encoding.UTF8.GetBytes(csv));

    private AggTradesArchiveMaterializer Materializer() => new(
        _archive, new PartitionFileWriter(), _schema, _statusStore,
        NullLogger<AggTradesArchiveMaterializer>.Instance);

    [Fact]
    public async Task MaterializeMonth_Futures_SplitsByDay_ScaledLongs()
    {
        _archive.DownloadMonthly("futures/um", "aggTrades", "BTCUSDT", null, 2024, 3, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream?>(CsvStream(FuturesAgg)));

        var result = await Materializer().MaterializeMonth(FuturesConfig(2), FeedConfig(), _dir, 2024, 3, Ct);

        Assert.Equal(3, result.RowsWritten);
        Assert.True(result.AvailableAtSource);

        var day1 = Path.Combine(_dir, "ticks", "2024-03-01.csv");
        var lines1 = await File.ReadAllLinesAsync(day1, Ct);
        Assert.Equal("ts,price,qty,is_buyer_maker,agg_id", lines1[0]);
        // price 50000.5*100=5000050 ; qty 0.100*100=10 ; "true"→1 ; agg_id 100
        Assert.Equal("1709251200000,5000050,10,1,100", lines1[1]);
        Assert.Equal("1709251260000,5000100,20,0,101", lines1[2]);

        var day2 = Path.Combine(_dir, "ticks", "2024-03-02.csv");
        var lines2 = await File.ReadAllLinesAsync(day2, Ct);
        Assert.Equal("1709337600000,5000200,5,1,102", lines2[1]);
    }

    [Fact]
    public async Task MaterializeMonth_Spot_Microseconds_Normalized()
    {
        _archive.DownloadMonthly("spot", "aggTrades", "BTCUSDT", null, 2025, 1, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream?>(CsvStream(SpotAggMicros)));

        var result = await Materializer().MaterializeMonth(SpotConfig(2), FeedConfig(), _dir, 2025, 1, Ct);

        Assert.Equal(1, result.RowsWritten);
        var path = Path.Combine(_dir, "ticks", "2025-01-01.csv");
        var lines = await File.ReadAllLinesAsync(path, Ct);
        // 1735689600000000 µs → 1735689600000 ms ; is_best_match column dropped ; "false"→0
        Assert.Equal("1735689600000,6000000,1,0,1000", lines[1]);
    }

    [Fact]
    public async Task MaterializeMonth_FromMonthlyZip_MarksCompleteMonth()
    {
        _archive.DownloadMonthly("futures/um", "aggTrades", "BTCUSDT", null, 2024, 3, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream?>(CsvStream(FuturesAgg)));

        await Materializer().MaterializeMonth(FuturesConfig(2), FeedConfig(), _dir, 2024, 3, Ct);

        await _statusStore.Received().Save(
            _dir, FeedNames.Ticks, "",
            Arg.Is<FeedStatus>(s => s.CompleteMonths.Contains("2024-03")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MaterializeMonth_AssembledFromDailies_DoesNotMarkComplete()
    {
        _archive.DownloadMonthly(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream?>(null));
        _archive.DownloadDaily("futures/um", "aggTrades", "BTCUSDT", null, new DateOnly(2024, 3, 1), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream?>(CsvStream(
                "100,50000.5,0.100,1,1,1709251200000,true\n")));

        var result = await Materializer().MaterializeMonth(FuturesConfig(2), FeedConfig(), _dir, 2024, 3, Ct);

        Assert.True(result.AvailableAtSource);
        Assert.Equal(1, result.RowsWritten);
        Assert.True(File.Exists(Path.Combine(_dir, "ticks", "2024-03-01.csv")));

        // No CompleteMonths marker for a month assembled from dailies.
        await _statusStore.DidNotReceive().Save(
            _dir, FeedNames.Ticks, "",
            Arg.Is<FeedStatus>(s => s.CompleteMonths.Count > 0),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MaterializeMonth_DedupsByAggId()
    {
        const string dupAgg =
            "100,50000.5,0.100,1,1,1709251200000,true\n" +
            "100,50000.5,0.100,1,1,1709251200000,true\n" +  // duplicate agg_id 100
            "101,50001.0,0.200,2,2,1709251260000,false\n";
        _archive.DownloadMonthly("futures/um", "aggTrades", "BTCUSDT", null, 2024, 3, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream?>(CsvStream(dupAgg)));

        var result = await Materializer().MaterializeMonth(FuturesConfig(2), FeedConfig(), _dir, 2024, 3, Ct);

        Assert.Equal(2, result.RowsWritten);
        var lines = await File.ReadAllLinesAsync(Path.Combine(_dir, "ticks", "2024-03-01.csv"), Ct);
        Assert.Equal(3, lines.Length); // header + 2 rows
        Assert.Equal("1709251200000,5000050,10,1,100", lines[1]);
        Assert.Equal("1709251260000,5000100,20,0,101", lines[2]);
    }

    [Fact]
    public async Task MaterializeMonth_MonthlyZipPresentButEmpty_DoesNotMarkCompleteMonth()
    {
        // Available-but-empty: a monthly zip exists but yields zero data rows (header only).
        _archive.DownloadMonthly("futures/um", "aggTrades", "BTCUSDT", null, 2024, 3, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream?>(CsvStream(
                "agg_trade_id,price,quantity,first_trade_id,last_trade_id,transact_time,is_buyer_maker\n")));

        var result = await Materializer().MaterializeMonth(FuturesConfig(2), FeedConfig(), _dir, 2024, 3, Ct);

        Assert.Equal(0, result.RowsWritten);
        Assert.True(result.AvailableAtSource);

        await _statusStore.DidNotReceive().Save(
            _dir, FeedNames.Ticks, "",
            Arg.Is<FeedStatus>(s => s.CompleteMonths.Count > 0),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MaterializeMonth_DropsOutOfMonthRows()
    {
        // A leading prior-month row (2024-02-29 23:59) spills into the March monthly zip.
        const string spillAgg =
            "99,49999.0,0.100,1,1,1709251140000,true\n" +   // 2024-02-29 23:59 (prior month)
            "100,50000.5,0.100,2,2,1709251200000,true\n" +  // 2024-03-01 00:00
            "101,50001.0,0.200,3,3,1709251260000,false\n";  // 2024-03-01 00:01
        _archive.DownloadMonthly("futures/um", "aggTrades", "BTCUSDT", null, 2024, 3, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream?>(CsvStream(spillAgg)));

        var result = await Materializer().MaterializeMonth(FuturesConfig(2), FeedConfig(), _dir, 2024, 3, Ct);

        Assert.Equal(2, result.RowsWritten);
        Assert.False(File.Exists(Path.Combine(_dir, "ticks", "2024-02-29.csv")));

        var lines = await File.ReadAllLinesAsync(Path.Combine(_dir, "ticks", "2024-03-01.csv"), Ct);
        Assert.Equal(3, lines.Length); // header + 2 in-month rows
        Assert.Equal("1709251200000,5000050,10,1,100", lines[1]);
        Assert.Equal("1709251260000,5000100,20,0,101", lines[2]);
    }

    [Fact]
    public async Task MaterializeMonth_NonMonotonicDayRegression_Throws()
    {
        // agg_id increases but the UTC day jumps backward (2024-03-02 → 2024-03-01).
        const string backwardAgg =
            "100,50000.0,0.100,1,1,1709337600000,true\n" +   // 2024-03-02 00:00
            "101,50001.0,0.100,2,2,1709251200000,false\n";   // 2024-03-01 00:00 (earlier day!)
        _archive.DownloadMonthly("futures/um", "aggTrades", "BTCUSDT", null, 2024, 3, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream?>(CsvStream(backwardAgg)));

        await Assert.ThrowsAsync<ArchiveIntegrityException>(
            () => Materializer().MaterializeMonth(FuturesConfig(2), FeedConfig(), _dir, 2024, 3, Ct));
    }

    [Fact]
    public async Task AggTrades_TwoMonthMaterialization_BothInCompleteMonths()
    {
        var store = new FeedStatusManager(new LocalFileStorage());
        var mat = new AggTradesArchiveMaterializer(
            _archive, new PartitionFileWriter(), _schema, store,
            NullLogger<AggTradesArchiveMaterializer>.Instance);

        _archive.DownloadMonthly("futures/um", "aggTrades", "BTCUSDT", null, 2024, 1, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream?>(CsvStream("100,50000.0,0.100,1,1,1704067200000,true\n"))); // 2024-01-01
        _archive.DownloadMonthly("futures/um", "aggTrades", "BTCUSDT", null, 2024, 2, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream?>(CsvStream("200,51000.0,0.100,1,1,1706745600000,true\n"))); // 2024-02-01

        await mat.MaterializeMonth(FuturesConfig(2), FeedConfig(), _dir, 2024, 1, Ct);
        await mat.MaterializeMonth(FuturesConfig(2), FeedConfig(), _dir, 2024, 2, Ct);

        var status = await store.Load(_dir, FeedNames.Ticks, "", Ct);
        Assert.NotNull(status);
        Assert.Equal(new[] { "2024-01", "2024-02" }, status.CompleteMonths);
    }

    [Fact]
    public async Task MaterializeMonth_AssembledFromDailies_TwoDays_WritesEachDayFile()
    {
        _archive.DownloadMonthly(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream?>(null));
        _archive.DownloadDaily("futures/um", "aggTrades", "BTCUSDT", null, new DateOnly(2024, 3, 1), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream?>(CsvStream("100,50000.0,0.100,1,1,1709251200000,true\n")));
        _archive.DownloadDaily("futures/um", "aggTrades", "BTCUSDT", null, new DateOnly(2024, 3, 2), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream?>(CsvStream("101,50002.0,0.050,3,3,1709337600000,true\n")));

        var result = await Materializer().MaterializeMonth(FuturesConfig(2), FeedConfig(), _dir, 2024, 3, Ct);

        Assert.Equal(2, result.RowsWritten);
        Assert.True(File.Exists(Path.Combine(_dir, "ticks", "2024-03-01.csv")));
        Assert.True(File.Exists(Path.Combine(_dir, "ticks", "2024-03-02.csv")));
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

        var result = await Materializer().MaterializeMonth(FuturesConfig(2), FeedConfig(), _dir, 2024, 3, Ct);

        Assert.False(result.AvailableAtSource);
        Assert.Equal(0, result.RowsWritten);
        Assert.False(Directory.Exists(Path.Combine(_dir, "ticks")));
    }
}
