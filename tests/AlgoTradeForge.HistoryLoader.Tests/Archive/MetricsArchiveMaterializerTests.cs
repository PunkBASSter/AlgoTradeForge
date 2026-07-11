using System.Globalization;
using System.Text;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Archive;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Infrastructure.Archive;
using AlgoTradeForge.HistoryLoader.Tests.TestData;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Archive;

public sealed class MetricsArchiveMaterializerTests : IDisposable
{
    private const string MetricsCsv =
        "create_time,symbol,sum_open_interest,sum_open_interest_value,count_toptrader_long_short_ratio,sum_toptrader_long_short_ratio,count_long_short_ratio,sum_taker_long_short_vol_ratio\n" +
        "2024-03-01 00:00:00,BTCUSDT,108532.354,6370849179.8,2.96564793,1.303872,2.84772561,1.27027\n" +
        "2024-03-01 00:05:00,BTCUSDT,108533.926,6363680536.55,2.96809221,1.303266,2.85246656,0.654691\n" +
        "2024-03-01 00:15:00,BTCUSDT,108465.299,6358363944.54,2.96854526,1.301941,2.85154501,0.517488\n";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"atf-metrics-{Guid.NewGuid():N}");
    private readonly IBinanceArchiveClient _archive = Substitute.For<IBinanceArchiveClient>();
    private readonly ISchemaManager _schema = Substitute.For<ISchemaManager>();
    private readonly IFeedStatusStore _statusStore = Substitute.For<IFeedStatusStore>();

    public MetricsArchiveMaterializerTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static CollectionAsset FuturesConfig() => CollectionAssets.Perp("BTCUSDT", 2);

    private static CollectionFeed FeedCfg(string name, string interval) =>
        CollectionAssets.Feed(name, interval);

    private static Stream CsvStream(string csv) => new MemoryStream(Encoding.UTF8.GetBytes(csv));

    private MetricsArchiveMaterializer Sut(string feedName) => new(
        feedName, _archive, new PartitionFileWriter(), _schema, _statusStore,
        NullLogger<MetricsArchiveMaterializer>.Instance);

    [Fact]
    public async Task OpenInterest_WritesOiRows_AtConfiguredInterval()
    {
        // interval "15m": rows at 00:00 and 00:15 kept; 00:05 dropped
        _archive.DownloadDaily("futures/um", "metrics", "BTCUSDT", null, new DateOnly(2024, 3, 1), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream?>(CsvStream(MetricsCsv)));

        var result = await Sut(FeedNames.OpenInterest).MaterializeMonth(
            FuturesConfig(), FeedCfg(FeedNames.OpenInterest, "15m"), _dir, 2024, 3,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, result.RowsWritten);
        Assert.True(result.AvailableAtSource);

        var path = Path.Combine(_dir, "open-interest", "2024-03_15m.csv");
        Assert.True(File.Exists(path));
        var lines = await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal("ts,oi,oi_usd", lines[0]);
        Assert.Equal("1709251200000,108532.354,6370849179.8", lines[1]);  // 00:00
        Assert.Equal("1709252100000,108465.299,6358363944.54", lines[2]); // 00:15
    }

    [Fact]
    public async Task LsRatioGlobal_DerivesPctFromRatio()
    {
        // interval "5m": all 3 rows pass; verify long_pct and short_pct from first row
        _archive.DownloadDaily("futures/um", "metrics", "BTCUSDT", null, new DateOnly(2024, 3, 1), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream?>(CsvStream(MetricsCsv)));

        await Sut(FeedNames.LsRatioGlobal).MaterializeMonth(
            FuturesConfig(), FeedCfg(FeedNames.LsRatioGlobal, "5m"), _dir, 2024, 3,
            TestContext.Current.CancellationToken);

        var path = Path.Combine(_dir, "ls-ratio-global", "2024-03_5m.csv");
        var lines = await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken);
        var parts = lines[1].Split(',');
        var actualLongPct = double.Parse(parts[1], CultureInfo.InvariantCulture);
        var actualShortPct = double.Parse(parts[2], CultureInfo.InvariantCulture);

        // First row: count_long_short_ratio (col 6) = 2.84772561
        const double ratio = 2.84772561;
        Assert.Equal(ratio / (1.0 + ratio), actualLongPct, precision: 10);
        Assert.Equal(1.0 / (1.0 + ratio), actualShortPct, precision: 10);
    }

    [Fact]
    public async Task MissingDay_RecordsPresentToPresentGap_AndContinues()
    {
        // Day 1 last row ts = 2024-03-01 23:55:00 UTC = 1709337300000
        // Day 3 first row ts = 2024-03-03 00:00:00 UTC = 1709424000000
        // Gap span = 86700000 ms; for 5m (300000 ms): 289 intervals → 288 missing rows (one full day)
        const long day1LastTs = 1709337300000L;
        const long day3FirstTs = 1709424000000L;

        const string day1Csv =
            "create_time,symbol,sum_open_interest,sum_open_interest_value,count_toptrader_long_short_ratio,sum_toptrader_long_short_ratio,count_long_short_ratio,sum_taker_long_short_vol_ratio\n" +
            "2024-03-01 23:55:00,BTCUSDT,108532.354,6370849179.8,2.96564793,1.303872,2.84772561,1.27027\n";
        const string day3Csv =
            "create_time,symbol,sum_open_interest,sum_open_interest_value,count_toptrader_long_short_ratio,sum_toptrader_long_short_ratio,count_long_short_ratio,sum_taker_long_short_vol_ratio\n" +
            "2024-03-03 00:00:00,BTCUSDT,108532.354,6370849179.8,2.96564793,1.303872,2.84772561,1.27027\n";

        _archive.DownloadDaily("futures/um", "metrics", "BTCUSDT", null, new DateOnly(2024, 3, 1), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream?>(CsvStream(day1Csv)));
        _archive.DownloadDaily("futures/um", "metrics", "BTCUSDT", null, new DateOnly(2024, 3, 3), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream?>(CsvStream(day3Csv)));

        var result = await Sut(FeedNames.OpenInterest).MaterializeMonth(
            FuturesConfig(), FeedCfg(FeedNames.OpenInterest, "5m"), _dir, 2024, 3,
            TestContext.Current.CancellationToken);

        Assert.True(result.AvailableAtSource);
        await _statusStore.Received(1).Save(
            _dir, FeedNames.OpenInterest, "5m",
            Arg.Is<FeedStatus>(s =>
                s.Gaps.Count == 1 &&
                s.Gaps[0].FromMs == day1LastTs &&
                s.Gaps[0].ToMs == day3FirstTs),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AllDaysMissing_ReportsUnavailable()
    {
        // NSubstitute default for Task<Stream?> returns null — no setup needed

        var result = await Sut(FeedNames.OpenInterest).MaterializeMonth(
            FuturesConfig(), FeedCfg(FeedNames.OpenInterest, "5m"), _dir, 2024, 3,
            TestContext.Current.CancellationToken);

        Assert.False(result.AvailableAtSource);
        Assert.Equal(0, result.RowsWritten);
        Assert.False(Directory.Exists(Path.Combine(_dir, "open-interest")));
    }

    [Fact]
    public async Task TopAccounts_And_TopPositions_MapCorrectColumns()
    {
        // First MetricsCsv row: col4 = 2.96564793 (top-accounts ratio), col5 = 1.303872 (top-positions ratio)
        _archive.DownloadDaily("futures/um", "metrics", "BTCUSDT", null, new DateOnly(2024, 3, 1), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream?>(CsvStream(MetricsCsv)));

        await Sut(FeedNames.LsRatioTopAccounts).MaterializeMonth(
            FuturesConfig(), FeedCfg(FeedNames.LsRatioTopAccounts, "5m"), _dir, 2024, 3,
            TestContext.Current.CancellationToken);
        await Sut(FeedNames.LsRatioTopPositions).MaterializeMonth(
            FuturesConfig(), FeedCfg(FeedNames.LsRatioTopPositions, "5m"), _dir, 2024, 3,
            TestContext.Current.CancellationToken);

        var accLines = await File.ReadAllLinesAsync(
            Path.Combine(_dir, "ls-ratio-top-accounts", "2024-03_5m.csv"),
            TestContext.Current.CancellationToken);
        var posLines = await File.ReadAllLinesAsync(
            Path.Combine(_dir, "ls-ratio-top-positions", "2024-03_5m.csv"),
            TestContext.Current.CancellationToken);

        // ratio is 4th column (index 3) in "ts,long_pct,short_pct,ratio"
        var accRatio = double.Parse(accLines[1].Split(',')[3], CultureInfo.InvariantCulture);
        var posRatio = double.Parse(posLines[1].Split(',')[3], CultureInfo.InvariantCulture);

        Assert.Equal(2.96564793, accRatio, precision: 5);
        Assert.Equal(1.303872, posRatio, precision: 5);
    }

    [Fact]
    public void RejectsSpot()
    {
        var sut = Sut(FeedNames.OpenInterest);
        Assert.False(sut.Supports(AssetTypes.Spot));
        Assert.True(sut.Supports(AssetTypes.Perpetual));
    }
}
