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

        var covered = await sut.IsMonthCovered(_dir, "candles", "1h", 2024, 3, [], TestContext.Current.CancellationToken);

        Assert.False(covered);
    }

    [Fact]
    public async Task FullPastMonth_Covered()
    {
        // March 2024: 31 days × 24 hours = 744 rows.
        var monthStart = new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero);
        WritePartition("candles", 2024, 3, "1h", HourlyRows(monthStart.ToUnixTimeMilliseconds(), 744));

        var sut = BuildSut();
        var covered = await sut.IsMonthCovered(_dir, "candles", "1h", 2024, 3, [], TestContext.Current.CancellationToken);

        Assert.True(covered);
    }

    [Fact]
    public async Task PartialTail_NotCovered()
    {
        // 700 rows written for a 744-row month.
        var monthStart = new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero);
        WritePartition("candles", 2024, 3, "1h", HourlyRows(monthStart.ToUnixTimeMilliseconds(), 700));

        var sut = BuildSut();
        var covered = await sut.IsMonthCovered(_dir, "candles", "1h", 2024, 3, [], TestContext.Current.CancellationToken);

        Assert.False(covered);
    }

    [Fact]
    public async Task HoleInMiddle_NotCovered()
    {
        // 734 rows (744 - 10 removed from middle); no DataGap credit supplied.
        var monthStart = new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero);
        WritePartition("candles", 2024, 3, "1h", HourlyRows(monthStart.ToUnixTimeMilliseconds(), 734));

        var sut = BuildSut();
        var covered = await sut.IsMonthCovered(_dir, "candles", "1h", 2024, 3, [], TestContext.Current.CancellationToken);

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
        var covered = await sut.IsMonthCovered(_dir, "candles", "1h", 2024, 3, gaps, TestContext.Current.CancellationToken);

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
        var covered = await sut.IsMonthCovered(_dir, "candles", "1h", 2026, 7, [], TestContext.Current.CancellationToken);

        Assert.False(covered);
    }
}
