using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Infrastructure.Archive;
using AlgoTradeForge.HistoryLoader.Infrastructure.State;
using AlgoTradeForge.Storage;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Archive;

public sealed class ArchiveStatusMergerTests : IDisposable
{
    // 2020-02-01 00:00:00 UTC — same epoch as the smoke-confirmed bug month (BTCUSDT 1h Feb 2020).
    private static readonly long Base = new DateTimeOffset(2020, 2, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
    private const long HourMs = 3_600_000L;

    private readonly string _tempDir;
    private readonly FeedStatusManager _store;

    public ArchiveStatusMergerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ArchiveStatusMergerTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _store = new FeedStatusManager(new LocalFileStorage());
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // -----------------------------------------------------------------------
    // CompleteMonths marker + preservation across MergeStatus rebuild
    // -----------------------------------------------------------------------

    [Fact]
    public async Task MarkCompleteMonth_Adds_WhenAbsent_SortedOrdinal()
    {
        await _store.Save(_tempDir, FeedNames.Ticks, "", new FeedStatus
        { FeedName = FeedNames.Ticks, Interval = "", CompleteMonths = ["2024-03"] }, Ct);

        await ArchiveStatusMerger.MarkCompleteMonth(_store, _tempDir, FeedNames.Ticks, "", "2024-01", Ct);

        var loaded = await _store.Load(_tempDir, FeedNames.Ticks, "", Ct);
        Assert.Equal(new[] { "2024-01", "2024-03" }, loaded!.CompleteMonths);
    }

    [Fact]
    public async Task MarkCompleteMonth_Idempotent_WhenPresent()
    {
        await _store.Save(_tempDir, FeedNames.Ticks, "", new FeedStatus
        { FeedName = FeedNames.Ticks, Interval = "", CompleteMonths = ["2024-01"] }, Ct);

        await ArchiveStatusMerger.MarkCompleteMonth(_store, _tempDir, FeedNames.Ticks, "", "2024-01", Ct);

        var loaded = await _store.Load(_tempDir, FeedNames.Ticks, "", Ct);
        Assert.Equal(new[] { "2024-01" }, loaded!.CompleteMonths);
    }

    [Fact]
    public async Task MergeStatus_PreservesCompleteMonths()
    {
        // Cross-month data-loss guard: an earlier month's marker must survive a MergeStatus
        // rebuild for a later month. Without the fix the rebuild wipes it and this fails.
        await _store.Save(_tempDir, FeedNames.Ticks, "", new FeedStatus
        { FeedName = FeedNames.Ticks, Interval = "", CompleteMonths = ["2024-01"] }, Ct);

        var feb = new DateTimeOffset(2024, 2, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        await ArchiveStatusMerger.MergeStatus(_store, _tempDir, FeedNames.Ticks, "",
            feb, feb + HourMs, recordCountDelta: 10, newGaps: [], Ct);

        var loaded = await _store.Load(_tempDir, FeedNames.Ticks, "", Ct);
        Assert.Contains("2024-01", loaded!.CompleteMonths);
    }

    // -----------------------------------------------------------------------
    // CountDataRows streams line-by-line (never File.ReadAllLines) so multi-million-row
    // tick partitions are counted without materializing the whole file as string[].
    // -----------------------------------------------------------------------

    [Fact]
    public async Task CountDataRows_LargeFile_CountsWithoutReadAllLines()
    {
        const int dataRows = 200_000;
        var path = Path.Combine(_tempDir, "large.csv");
        await using (var writer = new StreamWriter(path))
        {
            await writer.WriteLineAsync("ts,price,qty,is_buyer_maker,agg_id");
            for (var i = 0; i < dataRows; i++)
                await writer.WriteLineAsync($"{Base + i},1,1,0,{i}");
        }

        var count = await ArchiveStatusMerger.CountDataRows(path, Ct);

        Assert.Equal(dataRows, count);
    }

    [Fact]
    public async Task CountDataRows_MissingFile_ReturnsZero()
    {
        var count = await ArchiveStatusMerger.CountDataRows(
            Path.Combine(_tempDir, "does-not-exist.csv"), Ct);
        Assert.Equal(0, count);
    }

    // -----------------------------------------------------------------------
    // DetectGaps — archive threshold is ANY missing slot (> 1×interval)
    // -----------------------------------------------------------------------

    [Fact]
    public void DetectGaps_ConsecutiveRows_NoGap()
    {
        var parsed = MakeRows([Base, Base + HourMs, Base + HourMs * 2]);
        var gaps = ArchiveStatusMerger.DetectGaps(parsed, HourMs);
        Assert.Empty(gaps);
    }

    [Fact]
    public void DetectGaps_SingleMissingSlot_RecordsOneGap()
    {
        // Jump of exactly 2×interval = one missing slot.
        // OLD behaviour (streaming multiplier=2.0): curr − prev = 2×interval is NOT > 2×interval → no gap.
        // NEW behaviour (archive exact threshold): curr − prev = 2×interval IS > 1×interval → gap recorded.
        var parsed = MakeRows([Base, Base + HourMs * 2]);

        var gaps = ArchiveStatusMerger.DetectGaps(parsed, HourMs);

        var gap = Assert.Single(gaps);
        Assert.Equal(Base, gap.FromMs);             // last PRESENT row before hole
        Assert.Equal(Base + HourMs * 2, gap.ToMs); // first PRESENT row after hole
    }

    [Fact]
    public void DetectGaps_MultiSlotHole_RecordsOneGap()
    {
        // 6×interval jump (the live-confirmed 2020-02-19 hole: 6 missing 5m rows).
        var intervalMs = 5 * 60_000L; // 5m
        var ts0 = Base;
        var ts1 = Base + intervalMs * 7; // skip 6 slots
        var parsed = MakeRows([ts0, ts1]);

        var gaps = ArchiveStatusMerger.DetectGaps(parsed, intervalMs);

        var gap = Assert.Single(gaps);
        Assert.Equal(ts0, gap.FromMs);
        Assert.Equal(ts1, gap.ToMs);
    }

    [Fact]
    public void DetectGaps_EmptyList_NoGap()
    {
        var gaps = ArchiveStatusMerger.DetectGaps([], HourMs);
        Assert.Empty(gaps);
    }

    [Fact]
    public void DetectGaps_SingleRow_NoGap()
    {
        var parsed = MakeRows([Base]);
        var gaps = ArchiveStatusMerger.DetectGaps(parsed, HourMs);
        Assert.Empty(gaps);
    }

    private static List<(long Ts, string[] Row)> MakeRows(long[] timestamps) =>
        timestamps.Select(ts => (ts, Array.Empty<string>())).ToList();
}
