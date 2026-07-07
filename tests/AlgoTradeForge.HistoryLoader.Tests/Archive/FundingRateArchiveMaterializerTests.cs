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

public sealed class FundingRateArchiveMaterializerTests : IDisposable
{
    private const string FundingCsv =
        "calc_time,funding_interval_hours,last_funding_rate\n" +
        "1709251200000,8,0.00010000\n" +   // 2024-03-01 00:00
        "1709280000000,8,0.00012000\n";    // 2024-03-01 08:00

    // markPriceKlines 8h: openTime,o,h,l,close,vol,closeTime,...
    private const string MarkKlines8h =
        "1709251200000,50000,50100,49900,50050,0,1709279999999,0,0,0,0,0\n" +
        "1709280000000,50050,50200,50000,50150,0,1709308799999,0,0,0,0,0\n";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"atf-fund-{Guid.NewGuid():N}");
    private readonly IBinanceArchiveClient _archive = Substitute.For<IBinanceArchiveClient>();
    private readonly ISchemaManager _schema = Substitute.For<ISchemaManager>();
    private readonly IFeedStatusStore _statusStore = Substitute.For<IFeedStatusStore>();

    public FundingRateArchiveMaterializerTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AssetCollectionConfig FuturesConfig() => new()
    {
        Symbol = "BTCUSDT",
        Type = AssetTypes.Perpetual,
        DecimalDigits = 2
    };

    private static FeedCollectionConfig FeedConfig() => new() { Name = FeedNames.FundingRate, Interval = "" };

    private static Stream CsvStream(string csv) => new MemoryStream(Encoding.UTF8.GetBytes(csv));

    private FundingRateArchiveMaterializer Materializer() => new(
        _archive, new PartitionFileWriter(), _schema, _statusStore,
        NullLogger<FundingRateArchiveMaterializer>.Instance);

    private void StubFunding(string csv) =>
        _archive.DownloadMonthly("futures/um", "fundingRate", "BTCUSDT", null, 2024, 3, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream?>(CsvStream(csv)));

    private void StubMark(string csv) =>
        _archive.DownloadMonthly("futures/um", "markPriceKlines", "BTCUSDT", "8h", 2024, 3, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream?>(CsvStream(csv)));

    [Fact]
    public async Task MaterializeMonth_JoinsMarkPriceClose_OnFundingBoundary()
    {
        StubFunding(FundingCsv);
        StubMark(MarkKlines8h);

        var result = await Materializer().MaterializeMonth(FuturesConfig(), FeedConfig(), _dir, 2024, 3, Ct);

        Assert.Equal(2, result.RowsWritten);
        Assert.True(result.AvailableAtSource);

        var path = Path.Combine(_dir, "funding-rate", "2024-03.csv");
        var lines = await File.ReadAllLinesAsync(path, Ct);
        Assert.Equal("ts,rate,mark_price", lines[0]);
        Assert.Equal("1709251200000,0.0001,50050", lines[1]);
        Assert.Equal("1709280000000,0.00012,50150", lines[2]);
    }

    [Fact]
    public async Task MaterializeMonth_MissingMarkClose_CarriesForward()
    {
        StubFunding(FundingCsv);
        // Only the first mark kline present → second funding row carries 50050 forward.
        StubMark("1709251200000,50000,50100,49900,50050,0,1709279999999,0,0,0,0,0\n");

        await Materializer().MaterializeMonth(FuturesConfig(), FeedConfig(), _dir, 2024, 3, Ct);

        var lines = await File.ReadAllLinesAsync(Path.Combine(_dir, "funding-rate", "2024-03.csv"), Ct);
        Assert.Equal("1709251200000,0.0001,50050", lines[1]);
        Assert.Equal("1709280000000,0.00012,50050", lines[2]);
    }

    [Fact]
    public void MaterializeMonth_RejectsSpot() =>
        Assert.False(Materializer().Supports(AssetTypes.Spot));

    [Fact]
    public async Task MaterializeMonth_NoFundingAtSource_ReportsUnavailable()
    {
        _archive.DownloadMonthly(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream?>(null));
        _archive.DownloadDaily(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream?>(null));

        var result = await Materializer().MaterializeMonth(FuturesConfig(), FeedConfig(), _dir, 2024, 3, Ct);

        Assert.False(result.AvailableAtSource);
        Assert.Equal(0, result.RowsWritten);
        Assert.False(Directory.Exists(Path.Combine(_dir, "funding-rate")));
    }

    [Fact]
    public async Task MaterializeMonth_EnsuresAutoApplySpec()
    {
        StubFunding(FundingCsv);
        StubMark(MarkKlines8h);

        await Materializer().MaterializeMonth(FuturesConfig(), FeedConfig(), _dir, 2024, 3, Ct);

        await _schema.Received(1).EnsureSchema(
            _dir, FeedNames.FundingRate, "",
            Arg.Is<string[]>(c => c.Length == 2 && c[0] == "rate" && c[1] == "mark_price"),
            Arg.Is<AutoApplySpec>(a => a != null && a.Type == "FundingRate" && a.RateColumn == "rate"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MaterializeMonth_FromMonthlyZip_MarksCompleteMonth()
    {
        StubFunding(FundingCsv);
        StubMark(MarkKlines8h);

        await Materializer().MaterializeMonth(FuturesConfig(), FeedConfig(), _dir, 2024, 3, Ct);

        await _statusStore.Received().Save(
            _dir, FeedNames.FundingRate, "",
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
        _archive.DownloadDaily("futures/um", "fundingRate", "BTCUSDT", null, new DateOnly(2024, 3, 1), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream?>(CsvStream("1709251200000,8,0.00010000\n")));
        _archive.DownloadDaily("futures/um", "markPriceKlines", "BTCUSDT", "8h", new DateOnly(2024, 3, 1), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream?>(CsvStream("1709251200000,50000,50100,49900,50050,0,1709279999999,0,0,0,0,0\n")));

        var result = await Materializer().MaterializeMonth(FuturesConfig(), FeedConfig(), _dir, 2024, 3, Ct);

        Assert.True(result.AvailableAtSource);
        Assert.Equal(1, result.RowsWritten);
        Assert.True(File.Exists(Path.Combine(_dir, "funding-rate", "2024-03.csv")));

        await _statusStore.DidNotReceive().Save(
            _dir, FeedNames.FundingRate, "",
            Arg.Is<FeedStatus>(s => s.CompleteMonths.Count > 0),
            Arg.Any<CancellationToken>());
    }
}
