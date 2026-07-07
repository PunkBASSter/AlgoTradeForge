using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Infrastructure.Archive;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Archive;

public sealed class ArchiveStatusMergerTests
{
    // 2020-02-01 00:00:00 UTC — same epoch as the smoke-confirmed bug month (BTCUSDT 1h Feb 2020).
    private static readonly long Base = new DateTimeOffset(2020, 2, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
    private const long HourMs = 3_600_000L;

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
