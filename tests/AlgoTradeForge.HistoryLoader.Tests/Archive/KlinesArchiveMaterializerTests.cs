using System.Text;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Archive;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Infrastructure.Archive;
using AlgoTradeForge.HistoryLoader.Tests.TestData;
using AlgoTradeForge.HistoryLoader.Tests.TestHelpers;
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

    private static CollectionAsset SpotConfig(int decimalDigits = 2) =>
        CollectionAssets.Spot("BTCUSDT", decimalDigits);

    private static CollectionAsset FuturesConfig(int decimalDigits = 2) =>
        CollectionAssets.Perp("BTCUSDT", decimalDigits);

    private static CollectionFeed FeedConfig(string interval = "1h") =>
        CollectionAssets.Feed(FeedNames.Candles, interval);

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
    public async Task Materializer_DoesNotReplace_WhenNewRowsFewer()
    {
        // Replace-guard (M6): a sparse 2-row archive month must not clobber a fuller
        // 744-row REST-collected partition. Skips replace + status merge; reports (0, available).
        var candlesDir = Path.Combine(_dir, "candles");
        Directory.CreateDirectory(candlesDir);
        var path = Path.Combine(candlesDir, "2024-03_1h.csv");
        var existing = new[] { "ts,o,h,l,c,vol" }
            .Concat(Enumerable.Range(0, 744)
                .Select(i => $"{1709251200000L + (long)i * 3_600_000},1,1,1,1,1"));
        await File.WriteAllLinesAsync(path, existing, TestContext.Current.CancellationToken);
        var before = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);

        _archive.DownloadMonthly("spot", "klines", "BTCUSDT", "1h", 2024, 3, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream?>(CsvStream(KlineCsv)));

        var result = await CandlesMaterializer().MaterializeMonth(
            SpotConfig(), FeedConfig(), _dir, 2024, 3, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.RowsWritten);
        Assert.True(result.AvailableAtSource);
        var after = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(before, after);
        await _statusStore.DidNotReceiveWithAnyArgs().Update(default!, default!, default!, default!, default!);
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

        var captured = _statusStore.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == "Update")
            .Select(c => ((Func<FeedStatus?, FeedStatus>)c.GetArguments()[3]!)(null))
            .Single();
        Assert.Equal(2, captured.RecordCount);
        Assert.Equal(1709254800000, captured.LastTimestamp);
    }

    [Fact]
    public async Task MaterializeMonth_Twice_RecordCountNotDoubled()
    {
        // Partitions are REPLACED on re-materialization, so RecordCount must reflect the
        // partition's rows once — not accumulate per materialization.
        _archive.DownloadMonthly("spot", "klines", "BTCUSDT", "1h", 2024, 3, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream?>(CsvStream(KlineCsv)));

        // Emulate the atomic RMW: each Update applies the mutator to the running persisted state,
        // exactly as the real store's Load→mutate→write does under the per-path lock.
        FeedStatus? persisted = null;
        _statusStore.When(s => s.Update(
                _dir, FeedNames.Candles, "1h", Arg.Any<Func<FeedStatus?, FeedStatus>>(), Arg.Any<CancellationToken>()))
            .Do(ci => persisted = ci.ArgAt<Func<FeedStatus?, FeedStatus>>(3)(persisted));

        await CandlesMaterializer().MaterializeMonth(
            SpotConfig(), FeedConfig(), _dir, 2024, 3, TestContext.Current.CancellationToken);
        await CandlesMaterializer().MaterializeMonth(
            SpotConfig(), FeedConfig(), _dir, 2024, 3, TestContext.Current.CancellationToken);

        Assert.NotNull(persisted);
        Assert.Equal(2, persisted!.RecordCount);
    }

    [Fact]
    public async Task MaterializeMonth_MissingMiddleDay_RecordsGap()
    {
        // Day 1 (Mar 01): ts=1709251200000 (00:00 UTC)
        // Day 3 (Mar 03): ts=1709424000000 (00:00 UTC) — Mar 02 missing
        const string day1Csv =
            "1709251200000,50000.1,50100.2,49900.3,50050.4,12.5,1709254799999,625631.2,1500,6.25,312815.6,0\n";
        const string day3Csv =
            "1709424000000,50050.4,50200.0,50000.0,50150.0,10.0,1709427599999,501500.0,1200,5.0,250750.0,0\n";
        const long day1Ts = 1709251200000L;
        const long day3Ts = 1709424000000L;

        _archive.DownloadMonthly(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream?>(null));
        _archive.DownloadDaily("spot", "klines", "BTCUSDT", "1h", new DateOnly(2024, 3, 1), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream?>(CsvStream(day1Csv)));
        _archive.DownloadDaily("spot", "klines", "BTCUSDT", "1h", new DateOnly(2024, 3, 3), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream?>(CsvStream(day3Csv)));

        await CandlesMaterializer().MaterializeMonth(
            SpotConfig(), FeedConfig("1h"), _dir, 2024, 3, TestContext.Current.CancellationToken);

        // Gap between end of day-1 row and start of day-3 row must be recorded.
        var captured = _statusStore.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == "Update")
            .Select(c => ((Func<FeedStatus?, FeedStatus>)c.GetArguments()[3]!)(null))
            .Single();
        var gap = Assert.Single(captured.Gaps);
        Assert.Equal(day1Ts, gap.FromMs);
        Assert.Equal(day3Ts, gap.ToMs);
    }

    // -----------------------------------------------------------------------
    // I1-RESIDUAL regression pin: single-slot source hole (jump = 2×interval)
    // must be recorded as a DataGap and make IsMonthCovered return TRUE —
    // eliminating the eternal re-materialization observed live (BTCUSDT 1h 2020-02).
    // -----------------------------------------------------------------------

    [Fact]
    public async Task MaterializeMonth_SingleMissingSlot_RecordsGapAndMonthIsCovered()
    {
        // Feb 2024 (29 days × 24h = 696 expected 1h rows).
        // Build a CSV with 695 rows: row index 100 is omitted, producing a 2×interval jump
        // from ts[99] to ts[101] — a single missing slot.
        const long HourMs = 3_600_000L;
        var originMs = new DateTimeOffset(2024, 2, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        const int TotalRows = 696;

        var klineRows = Enumerable.Range(0, TotalRows)
            .Where(i => i != 100)
            .Select(i =>
            {
                var ts = originMs + (long)i * HourMs;
                var closeTs = ts + HourMs - 1;
                return $"{ts},50000.0,50100.0,49900.0,50050.0,12.5,{closeTs},625631.2,1500,6.25,312815.6,0";
            });
        var csv = string.Join("\n", klineRows) + "\n";

        _archive.DownloadMonthly("spot", "klines", "BTCUSDT", "1h", 2024, 2, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream?>(CsvStream(csv)));

        FeedStatus? savedStatus = null;
        _statusStore.When(s => s.Update(
                _dir, FeedNames.Candles, "1h", Arg.Any<Func<FeedStatus?, FeedStatus>>(), Arg.Any<CancellationToken>()))
            .Do(ci => savedStatus = ci.ArgAt<Func<FeedStatus?, FeedStatus>>(3)(null));

        await CandlesMaterializer().MaterializeMonth(
            SpotConfig(), FeedConfig("1h"), _dir, 2024, 2, TestContext.Current.CancellationToken);

        // Exactly one gap must be recorded: the single missing slot at index 100.
        Assert.NotNull(savedStatus);
        var gap = Assert.Single(savedStatus!.Gaps);
        Assert.Equal(originMs + 99L * HourMs, gap.FromMs); // last present row before hole
        Assert.Equal(originMs + 101L * HourMs, gap.ToMs);  // first present row after hole

        // Regression pin: with the gap credited, IsMonthCovered must return TRUE.
        // actualRows=695 + gapCredit=1 = 696 = expected → covered.
        var clock = new TestClock(new DateTimeOffset(2026, 7, 7, 0, 0, 0, TimeSpan.Zero));
        var covered = await new MonthCoverageCalculator(clock)
            .IsMonthCovered(
                _dir, FeedNames.Candles, "1h", 2024, 2, savedStatus.Gaps,
                new MonthPartitionRow("2024-02", 695, 0, ""),
                ct: TestContext.Current.CancellationToken);
        Assert.True(covered);
    }
}
