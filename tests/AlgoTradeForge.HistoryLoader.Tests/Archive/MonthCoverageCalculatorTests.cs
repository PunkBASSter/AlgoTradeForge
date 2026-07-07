using AlgoTradeForge.HistoryLoader.Application.Archive;
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

    // Generates count hourly candle rows starting from originMs.
    private static IEnumerable<string> HourlyRows(long originMs, int count) =>
        Enumerable.Range(0, count).Select(i => $"{originMs + (long)i * 3_600_000},100,105,95,102,1000");

    [Fact]
    public async Task MissingPartition_NotCovered()
    {
        var sut = BuildSut();

        var covered = await sut.IsMonthCovered(_dir, "candles", "1h", 2024, 3, [], ct: TestContext.Current.CancellationToken);

        Assert.False(covered);
    }

    [Fact]
    public async Task FullPastMonth_Covered()
    {
        // March 2024: 31 days × 24 hours = 744 rows.
        var monthStart = new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero);
        WritePartition("candles", 2024, 3, "1h", HourlyRows(monthStart.ToUnixTimeMilliseconds(), 744));

        var sut = BuildSut();
        var covered = await sut.IsMonthCovered(_dir, "candles", "1h", 2024, 3, [], ct: TestContext.Current.CancellationToken);

        Assert.True(covered);
    }

    [Fact]
    public async Task PartialTail_NotCovered()
    {
        // 700 rows written for a 744-row month.
        var monthStart = new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero);
        WritePartition("candles", 2024, 3, "1h", HourlyRows(monthStart.ToUnixTimeMilliseconds(), 700));

        var sut = BuildSut();
        var covered = await sut.IsMonthCovered(_dir, "candles", "1h", 2024, 3, [], ct: TestContext.Current.CancellationToken);

        Assert.False(covered);
    }

    [Fact]
    public async Task HoleInMiddle_NotCovered()
    {
        // 734 rows (744 - 10 removed from middle); no DataGap credit supplied.
        var monthStart = new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero);
        WritePartition("candles", 2024, 3, "1h", HourlyRows(monthStart.ToUnixTimeMilliseconds(), 734));

        var sut = BuildSut();
        var covered = await sut.IsMonthCovered(_dir, "candles", "1h", 2024, 3, [], ct: TestContext.Current.CancellationToken);

        Assert.False(covered);
    }

    [Fact]
    public async Task SourceGap_CountsTowardCoverage()
    {
        // 720 actual rows + one DataGap spanning 25 intervals → 24 gap-credit rows → 744 total.
        var monthStart = new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var originMs = monthStart.ToUnixTimeMilliseconds();
        WritePartition("candles", 2024, 3, "1h", HourlyRows(originMs, 720));

        // FromMs and ToMs are the last-present and first-present rows around the hole.
        // (ToMs - FromMs) / intervalMs - 1 = 25 - 1 = 24 credit rows.
        var gapFrom = originMs + 100L * 3_600_000;
        var gapTo = gapFrom + 25L * 3_600_000;
        var gaps = new[] { new DataGap { FromMs = gapFrom, ToMs = gapTo } };

        var sut = BuildSut();
        var covered = await sut.IsMonthCovered(_dir, "candles", "1h", 2024, 3, gaps, ct: TestContext.Current.CancellationToken);

        Assert.True(covered);
    }

    [Fact]
    public async Task CurrentMonth_NeverCovered()
    {
        // Clock is at 2026-07-07T00:00:00Z. July 2026 month start = 2026-07-01T00:00:00Z.
        // effectiveEnd = clock = monthStart + 6 days → expectedRows = 144.
        // Write only 143 rows → not covered.
        var monthStart = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
        WritePartition("candles", 2026, 7, "1h", HourlyRows(monthStart.ToUnixTimeMilliseconds(), 143));

        var sut = BuildSut(); // clock = 2026-07-07T00:00:00Z
        var covered = await sut.IsMonthCovered(_dir, "candles", "1h", 2026, 7, [], ct: TestContext.Current.CancellationToken);

        Assert.False(covered);
    }

    [Fact]
    public async Task EffectiveStart_ClampsListingMonth_ButHoleyMonthStaysUncovered()
    {
        var sut = BuildSut();
        var monthStartMs = new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        // Direction 1 (listing month): source data starts March 10 mid-month; full from there
        // to month end = 22 days × 24 = 528 rows. With effectiveStartMs = first data row,
        // expectation clamps to 528 → covered (no perpetual re-materialization).
        var firstDataMs = new DateTimeOffset(2024, 3, 10, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        WritePartition("candles", 2024, 3, "1h", HourlyRows(firstDataMs, 528));
        Assert.True(await sut.IsMonthCovered(
            _dir, "candles", "1h", 2024, 3, [], effectiveStartMs: firstDataMs, ct: TestContext.Current.CancellationToken));

        // Direction 2 (genuinely holey month): FirstTimestamp at month start, hole later —
        // 700 of 744 rows with effectiveStartMs = month start → still uncovered.
        WritePartition("mark-price", 2024, 3, "1h", HourlyRows(monthStartMs, 700));
        Assert.False(await sut.IsMonthCovered(
            _dir, "mark-price", "1h", 2024, 3, [], effectiveStartMs: monthStartMs, ct: TestContext.Current.CancellationToken));
    }

    // -------------------------------------------------------------------------
    // I1-residual: two canonical TDD cases per the fix brief.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GapCrossingMonthBoundary_CreditsClampedSlot()
    {
        // April 2024 (30 days = 720 hourly rows). A recorded gap runs from the last present
        // hour of March (2024-03-31 23:00) to the first present April row (2024-04-01 05:00).
        // Missing April slots inside the gap: 00:00..04:00 = 5. Partition holds 715 rows
        // from 05:00 on → 715 + 5 = 720 → covered. The old formula lost the slot AT the
        // clamp boundary (credited 4) and kept the month uncovered forever.
        var gapFrom = new DateTimeOffset(2024, 3, 31, 23, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var gapTo = new DateTimeOffset(2024, 4, 1, 5, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        WritePartition("candles", 2024, 4, "1h", HourlyRows(gapTo, 715));
        var gaps = new[] { new DataGap { FromMs = gapFrom, ToMs = gapTo } };

        var sut = BuildSut();
        var covered = await sut.IsMonthCovered(
            _dir, "candles", "1h", 2024, 4, gaps, ct: TestContext.Current.CancellationToken);

        Assert.True(covered);
    }

    [Fact]
    public async Task MonthEntirelyInsideGap_NoPartition_Covered()
    {
        // April 2024 lies entirely inside one recorded gap (last present row 2024-03-31 23:00,
        // next present row 2024-05-01 03:00). No partition file exists — correctly so — and the
        // gap credit alone must cover the month instead of retrying the archive forever.
        var gapFrom = new DateTimeOffset(2024, 3, 31, 23, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var gapTo = new DateTimeOffset(2024, 5, 1, 3, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var gaps = new[] { new DataGap { FromMs = gapFrom, ToMs = gapTo } };

        var sut = BuildSut();
        var covered = await sut.IsMonthCovered(
            _dir, "candles", "1h", 2024, 4, gaps, ct: TestContext.Current.CancellationToken);

        Assert.True(covered);
    }

    [Fact]
    public async Task ListingMonth_CoveredFromFirstDataTimestamp()
    {
        // March 2024: source data starts March 15 (408 hours remain to month end).
        // effectiveStartMs = march15Ms → expectedRows = 408; writing 408 rows → covered.
        var march15Ms = new DateTimeOffset(2024, 3, 15, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        WritePartition("candles", 2024, 3, "1h", HourlyRows(march15Ms, 408));

        var sut = BuildSut();
        var covered = await sut.IsMonthCovered(
            _dir, "candles", "1h", 2024, 3, [],
            effectiveStartMs: march15Ms, ct: TestContext.Current.CancellationToken);

        Assert.True(covered);
    }

    [Fact]
    public async Task EffectiveStart_AtMonthStart_HoleLater_StillUncovered()
    {
        // effectiveStartMs == monthStart: clamp is a no-op, full 744-row expectation applies.
        // 734 rows with no gap credit → not covered.
        var march1Ms = new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        WritePartition("candles", 2024, 3, "1h", HourlyRows(march1Ms, 734));

        var sut = BuildSut();
        var covered = await sut.IsMonthCovered(
            _dir, "candles", "1h", 2024, 3, [],
            effectiveStartMs: march1Ms, ct: TestContext.Current.CancellationToken);

        Assert.False(covered);
    }

    // -------------------------------------------------------------------------
    // Task 2: CompleteMonths predicate for interval-less feeds (ticks, funding-rate).
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Ticks_MonthInCompleteMonths_Covered()
    {
        var sut = BuildSut();
        Assert.True(await sut.IsMonthCovered(_dir, FeedNames.Ticks, "", 2024, 3, [],
            completeMonths: ["2024-03"], ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Ticks_MonthNotInCompleteMonths_NotCovered()
    {
        var sut = BuildSut();
        Assert.False(await sut.IsMonthCovered(_dir, FeedNames.Ticks, "", 2024, 3, [],
            completeMonths: ["2024-02"], ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Ticks_NullCompleteMonths_NotCovered()
    {
        var sut = BuildSut();
        Assert.False(await sut.IsMonthCovered(_dir, FeedNames.Ticks, "", 2024, 3, [],
            ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FundingRate_UsesCompleteMonths_NotRowCount()
    {
        // Critical regression guard: funding-rate is interval-less; IntervalParser.ToTimeSpan("")
        // must never be reached (throws). Coverage is purely the CompleteMonths marker.
        var sut = BuildSut();
        Assert.True(await sut.IsMonthCovered(_dir, FeedNames.FundingRate, "", 2024, 3, [],
            completeMonths: ["2024-03"], ct: TestContext.Current.CancellationToken));
        Assert.False(await sut.IsMonthCovered(_dir, FeedNames.FundingRate, "", 2024, 4, [],
            completeMonths: ["2024-03"], ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IntervalFeed_Unaffected_ByCompleteMonthsParam()
    {
        // completeMonths is ignored for interval feeds; row-count path still applies.
        var monthStart = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        WritePartition("candles", 2024, 1, "1h", HourlyRows(monthStart.ToUnixTimeMilliseconds(), 744));
        var sut = BuildSut();
        Assert.True(await sut.IsMonthCovered(_dir, "candles", "1h", 2024, 1, [],
            completeMonths: [], ct: TestContext.Current.CancellationToken));
    }

    // -------------------------------------------------------------------------
    // Streaming row-count cache (Task 5).
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RowCount_IsRecomputed_WhenPartitionFileChanges()
    {
        // January 2024: 31 days × 24 hours = 744 rows. Month is fully in the past.
        var monthStart = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var sut = BuildSut();

        WritePartition("candles", 2024, 1, "1h", HourlyRows(monthStart.ToUnixTimeMilliseconds(), 744));
        Assert.True(await sut.IsMonthCovered(_dir, "candles", "1h", 2024, 1, [], ct: TestContext.Current.CancellationToken));

        // Rewrite with far fewer rows → file length changes → cache entry invalidated.
        WritePartition("candles", 2024, 1, "1h", HourlyRows(monthStart.ToUnixTimeMilliseconds(), 10));
        Assert.False(await sut.IsMonthCovered(_dir, "candles", "1h", 2024, 1, [], ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RowCount_CacheHit_DoesNotReReadUnchangedFile()
    {
        // January 2024: 31 days × 24 hours = 744 rows. Month is fully in the past.
        var monthStart = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var sut = BuildSut();
        var partitionPath = Path.Combine(_dir, "candles", "2024-01_1h.csv");

        WritePartition("candles", 2024, 1, "1h", HourlyRows(monthStart.ToUnixTimeMilliseconds(), 744));
        Assert.True(await sut.IsMonthCovered(_dir, "candles", "1h", 2024, 1, [], ct: TestContext.Current.CancellationToken));

        // Exclusive lock held — a re-read would throw IOException; a cache hit must succeed.
        using var exclusive = new FileStream(partitionPath, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.True(await sut.IsMonthCovered(_dir, "candles", "1h", 2024, 1, [], ct: TestContext.Current.CancellationToken));
    }
}
