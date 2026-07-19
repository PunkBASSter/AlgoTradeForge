using AlgoTradeForge.HistoryLoader.Application.Archive;
using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Infrastructure.Archive;
using AlgoTradeForge.HistoryLoader.Tests.TestHelpers;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Archive;

public sealed class MonthCoverageCalculatorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"atf-mcc-{Guid.NewGuid():N}");

    // Clock pinned to 2026-07-07T00:00:00Z — well past any 2024 test data.
    private static readonly DateTimeOffset FixedNow = new(2026, 7, 7, 0, 0, 0, TimeSpan.Zero);

    public MonthCoverageCalculatorTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private IMonthCoverageCalculator BuildSut(DateTimeOffset? now = null)
    {
        var clock = new TestClock(now ?? FixedNow);
        return new MonthCoverageCalculator(clock);
    }

    // Creates {_dir}/{feedName}/{year:D4}-{month:D2}_{interval}.csv with a header row plus the
    // supplied data rows.
    private void WritePartition(string feedName, int year, int month, string interval, IEnumerable<string> dataRows)
    {
        var feedDir = Path.Combine(_dir, feedName);
        Directory.CreateDirectory(feedDir);
        var path = Path.Combine(feedDir, $"{year:D4}-{month:D2}_{interval}.csv");
        File.WriteAllLines(path, new[] { "ts,o,h,l,c,vol" }.Concat(dataRows));
    }

    [Fact]
    public async Task MissingPartition_NotCovered()
    {
        var sut = BuildSut();

        var covered = await sut.IsMonthCovered(
            _dir, "candles", "1h", 2024, 3, [], indexedMonth: null,
            ct: TestContext.Current.CancellationToken);

        Assert.False(covered);
    }

    // -------------------------------------------------------------------------
    // Fix B: coverage trusts the index row count; content is never re-read.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task IndexRowPresent_DrivesDecision_NotFileContent()
    {
        // File on disk has zero data rows (would read as "not covered" if content were read),
        // but the index row reports a covering count. The index must win — no content read.
        WritePartition(FeedNames.OpenInterest, 2021, 3, "5m", []);
        var indexed = new MonthPartitionRow("2021-03", Rows: 1_000_000, FileLen: 1, FileMtimeUtc: "x");

        var covered = await BuildSut().IsMonthCovered(
            _dir, FeedNames.OpenInterest, "5m", 2021, 3, [], indexed,
            ct: TestContext.Current.CancellationToken);

        Assert.True(covered);
    }

    [Fact]
    public async Task IndexRowBelowExpected_NotCovered()
    {
        var indexed = new MonthPartitionRow("2021-03", Rows: 10, FileLen: 1, FileMtimeUtc: "x");

        var covered = await BuildSut().IsMonthCovered(
            _dir, FeedNames.OpenInterest, "5m", 2021, 3, [], indexed,
            ct: TestContext.Current.CancellationToken);

        Assert.False(covered); // 10 << March's expected 5m slots (31*288)
    }

    [Fact]
    public async Task NoIndexRow_FileExists_DefersAsCovered()
    {
        WritePartition(FeedNames.OpenInterest, 2021, 3, "5m", ["1614556800000,1,2"]);

        var covered = await BuildSut().IsMonthCovered(
            _dir, FeedNames.OpenInterest, "5m", 2021, 3, [], indexedMonth: null,
            ct: TestContext.Current.CancellationToken);

        Assert.True(covered); // stale/cold index: defer, do not re-download
    }

    [Fact]
    public async Task NoIndexRow_NoFile_NotCovered()
    {
        var covered = await BuildSut().IsMonthCovered(
            _dir, FeedNames.OpenInterest, "5m", 2021, 3, [], indexedMonth: null,
            ct: TestContext.Current.CancellationToken);

        Assert.False(covered); // genuinely uncovered => materialize
    }

    // -------------------------------------------------------------------------
    // CompleteMonths predicate for interval-less feeds (ticks, funding-rate).
    // These route through the marker branch and never touch the file/index-count path.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Ticks_MonthInCompleteMonths_Covered()
    {
        var sut = BuildSut();
        Assert.True(await sut.IsMonthCovered(_dir, FeedNames.Ticks, "", 2024, 3, [], indexedMonth: null,
            completeMonths: ["2024-03"], ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Ticks_MonthNotInCompleteMonths_NotCovered()
    {
        var sut = BuildSut();
        Assert.False(await sut.IsMonthCovered(_dir, FeedNames.Ticks, "", 2024, 3, [], indexedMonth: null,
            completeMonths: ["2024-02"], ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Ticks_NullCompleteMonths_NotCovered()
    {
        var sut = BuildSut();
        Assert.False(await sut.IsMonthCovered(_dir, FeedNames.Ticks, "", 2024, 3, [], indexedMonth: null,
            ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FundingRate_UsesCompleteMonths_NotRowCount()
    {
        // Critical regression guard: funding-rate is interval-less; IntervalParser.ToTimeSpan("")
        // must never be reached (throws). Coverage is purely the CompleteMonths marker.
        var sut = BuildSut();
        Assert.True(await sut.IsMonthCovered(_dir, FeedNames.FundingRate, "", 2024, 3, [], indexedMonth: null,
            completeMonths: ["2024-03"], ct: TestContext.Current.CancellationToken));
        Assert.False(await sut.IsMonthCovered(_dir, FeedNames.FundingRate, "", 2024, 4, [], indexedMonth: null,
            completeMonths: ["2024-03"], ct: TestContext.Current.CancellationToken));
    }
}
