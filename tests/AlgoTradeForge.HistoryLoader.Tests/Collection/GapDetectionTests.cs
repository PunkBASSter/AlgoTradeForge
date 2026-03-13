using AlgoTradeForge.HistoryLoader.Binance;
using AlgoTradeForge.HistoryLoader.State;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Collection;

/// <summary>
/// Validates the gap detection logic used inside SymbolCollector.
/// Rather than spinning up the full collector with all I/O dependencies,
/// these tests exercise the core detection algorithm directly: given a
/// sequence of timestamps and an expected interval, verify that jumps
/// exceeding 2x the interval are recorded as DataGaps.
/// </summary>
public sealed class GapDetectionTests
{
    // -----------------------------------------------------------------------
    // Helper — mirrors the logic inside CollectGenericFeedAsync / CollectCandlesAsync
    // -----------------------------------------------------------------------

    private static List<DataGap> DetectGaps(IEnumerable<long> timestamps, string interval)
    {
        var gaps = new List<DataGap>();

        if (string.IsNullOrEmpty(interval))
            return gaps;

        var expectedMs = (long)BinanceIntervalMap.ToTimeSpan(interval).TotalMilliseconds;
        long previousTs = 0;

        foreach (var ts in timestamps)
        {
            if (previousTs > 0 && ts - previousTs > expectedMs * 2)
            {
                gaps.Add(new DataGap { FromMs = previousTs, ToMs = ts });
            }
            previousTs = ts;
        }

        return gaps;
    }

    // -----------------------------------------------------------------------
    // Tests
    // -----------------------------------------------------------------------

    // Use a realistic epoch base (2024-01-15 00:00:00 UTC) so timestamps are never 0.
    private static readonly long Base = new DateTimeOffset(2024, 1, 15, 0, 0, 0, TimeSpan.Zero)
        .ToUnixTimeMilliseconds();

    [Fact]
    public void DetectGaps_ConsecutiveTimestamps_NoGaps()
    {
        // 1-minute bars: base, base+1m, base+2m, base+3m  — no gaps
        long step = 60_000; // 1 minute
        var timestamps = Enumerable.Range(0, 4).Select(i => Base + i * step);

        var gaps = DetectGaps(timestamps, "1m");

        Assert.Empty(gaps);
    }

    [Fact]
    public void DetectGaps_TwoMissingBars_ReturnsOneGap()
    {
        // 1-minute bars: base, base+1m, base+4m (two bars missing at base+2m and base+3m)
        // Jump: (base+4m) - (base+1m) = 3 * 60_000ms = 3x expected → flagged
        long step = 60_000; // 1 minute
        var timestamps = new long[] { Base, Base + step, Base + step * 4 };

        var gaps = DetectGaps(timestamps, "1m");

        var gap = Assert.Single(gaps);
        Assert.Equal(Base + step, gap.FromMs);
        Assert.Equal(Base + step * 4, gap.ToMs);
    }

    [Fact]
    public void DetectGaps_ExactlyDoubleInterval_NoGap()
    {
        // Jump of exactly 2x interval should NOT be flagged (threshold is strictly >2x).
        // A single missing bar produces exactly a 2x jump.
        long step = 60_000; // 1 minute
        var timestamps = new long[] { Base, Base + step * 2 };  // delta = 2 * step = 2 * expectedMs

        var gaps = DetectGaps(timestamps, "1m");

        Assert.Empty(gaps);
    }

    [Fact]
    public void DetectGaps_MoreThanDoubleInterval_RecordsGap()
    {
        // Jump of 2x + 1ms is strictly > 2x and should be flagged
        long step = 60_000; // 1 minute
        var timestamps = new long[] { Base, Base + step * 2 + 1 };

        var gaps = DetectGaps(timestamps, "1m");

        var gap = Assert.Single(gaps);
        Assert.Equal(Base, gap.FromMs);
        Assert.Equal(Base + step * 2 + 1, gap.ToMs);
    }

    [Fact]
    public void DetectGaps_MultipleGaps_ReturnsAllGaps()
    {
        // 5-minute bars with two distinct gaps
        long step = 5 * 60_000; // 5 minutes
        var timestamps = new long[]
        {
            Base,
            Base + step,
            Base + step * 4,          // gap 1: Base+step → Base+step*4 (delta=3x)
            Base + step * 5,
            Base + step * 5 + step * 3 + 1,  // gap 2: delta = 3x + 1ms
        };

        var gaps = DetectGaps(timestamps, "5m");

        Assert.Equal(2, gaps.Count);
        Assert.Equal(Base + step, gaps[0].FromMs);
        Assert.Equal(Base + step * 4, gaps[0].ToMs);
    }

    [Fact]
    public void DetectGaps_EmptyInterval_SkipsGapDetection()
    {
        // Event-based feeds (e.g. funding-rate, liquidations) pass empty interval
        // → gap detection is disabled entirely regardless of timestamp jump size
        var timestamps = new long[] { Base, Base + 999_999_999 };

        var gaps = DetectGaps(timestamps, "");

        Assert.Empty(gaps);
    }

    [Fact]
    public void DetectGaps_SingleTimestamp_NoGaps()
    {
        var gaps = DetectGaps([Base], "1h");

        Assert.Empty(gaps);
    }

    [Fact]
    public void DetectGaps_AllSupportedIntervals_WorkCorrectly()
    {
        // Verify that BinanceIntervalMap.ToTimeSpan is used correctly for each interval.
        // For each interval, one "large" jump (> 2x) is introduced and should be detected.
        var intervalCases = new[]
        {
            ("1m",  60_000L),
            ("5m",  300_000L),
            ("1h",  3_600_000L),
            ("4h",  14_400_000L),
            ("1d",  86_400_000L),
        };

        foreach (var (interval, stepMs) in intervalCases)
        {
            // Normal consecutive bars: no gap
            var normal = new long[] { Base, Base + stepMs, Base + stepMs * 2 };
            Assert.Empty(DetectGaps(normal, interval));

            // Jump of strictly > 2x from the first bar: one gap detected
            var withGap = new long[] { Base, Base + stepMs * 2 + 1 };
            var detectedGaps = DetectGaps(withGap, interval);
            Assert.Single(detectedGaps);
        }
    }

    [Fact]
    public void DataGap_StoresFromAndToMs()
    {
        var gap = new DataGap { FromMs = 1_000L, ToMs = 5_000L };

        Assert.Equal(1_000L, gap.FromMs);
        Assert.Equal(5_000L, gap.ToMs);
    }
}
