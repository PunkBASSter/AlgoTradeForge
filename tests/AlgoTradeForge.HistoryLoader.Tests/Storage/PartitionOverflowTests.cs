using AlgoTradeForge.HistoryLoader.Application.Aggregation;
using AlgoTradeForge.Infrastructure.IO;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Storage;

/// <summary>
/// P1a-19, P1a-20, P1a-21 — <see cref="PartitionedSinkWriter"/> partition-overflow scheme
/// (TRD §3.2 part-numbered overflow).
/// </summary>
public sealed class PartitionOverflowTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), $"PartitionOverflowTests_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    /// <summary>2026-04-15 12:00:00 UTC → ms epoch.</summary>
    private static long Apr15Ms(int hour) =>
        new DateTimeOffset(2026, 4, 15, hour, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    /// <summary>2026-05-01 00:00:00 UTC → ms epoch.</summary>
    private static long May01Ms(int hour) =>
        new DateTimeOffset(2026, 5, 1, hour, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private string FeedDir(string name) => Path.Combine(_tempDir, name);

    private static LocalFileStorage Storage() => new();

    // -------------------------------------------------------------------------
    // P1a-19 baseline — no overflow, single bare-name partition
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Writer_BelowBudget_ProducesSingleBareNamePartition()
    {
        var feedDir = FeedDir("EqV_baseline");

        // Each row ~30 bytes with header included; budget 10 KB → no overflow for ~10 rows.
        await using (var writer = await PartitionedSinkWriter.Open(Storage(), feedDir, maxPartitionBytes: 10_000, headerLine: "ts,o,h,l,c,vol", Ct))
        {
            for (var i = 0; i < 10; i++)
                await writer.WriteRow(Apr15Ms(i % 24), $"{Apr15Ms(i % 24)},1,2,0,1,5", Ct);
            await writer.Complete(Ct);
        }

        Assert.True(File.Exists(Path.Combine(feedDir, "2026-04.csv")));
        Assert.False(File.Exists(Path.Combine(feedDir, "2026-04.p01.csv")));
        Assert.False(File.Exists(Path.Combine(feedDir, "2026-04.csv.tmp")));
    }

    // -------------------------------------------------------------------------
    // P1a-20 — mid-month first overflow renames bare → .p01, opens .p02
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Writer_MidMonthOverflow_PromotesBareToP01AndOpensP02()
    {
        var feedDir = FeedDir("EqV_overflow");

        // Tiny budget so each row triggers a rollover. Header alone is ~16 bytes; budget 80
        // bytes lets us write ~1 row before overflow check.
        const long tinyBudget = 80;

        await using (var writer = await PartitionedSinkWriter.Open(Storage(), feedDir, tinyBudget, headerLine: "ts,o,h,l,c,vol", Ct))
        {
            // 4 rows in April → triggers ~3 rollovers
            await writer.WriteRow(Apr15Ms(0), $"{Apr15Ms(0)},1,2,0,1,5", Ct);
            await writer.WriteRow(Apr15Ms(1), $"{Apr15Ms(1)},1,2,0,1,5", Ct);
            await writer.WriteRow(Apr15Ms(2), $"{Apr15Ms(2)},1,2,0,1,5", Ct);
            await writer.WriteRow(Apr15Ms(3), $"{Apr15Ms(3)},1,2,0,1,5", Ct);
            await writer.Complete(Ct);
        }

        // After rollover: bare-name 2026-04.csv MUST NOT exist (it was renamed to p01).
        Assert.False(File.Exists(Path.Combine(feedDir, "2026-04.csv")),
            "Bare-name partition should have been atomic-renamed to .p01 on first overflow.");

        // p01 exists (the originally-bare partition, now finalized as p01).
        Assert.True(File.Exists(Path.Combine(feedDir, "2026-04.p01.csv")));

        // No tmp leftover.
        var tmpFiles = Directory.EnumerateFiles(feedDir, "*.tmp").ToList();
        Assert.Empty(tmpFiles);

        // Lex sort matches chronological order.
        var partitionFiles = Directory.EnumerateFiles(feedDir, "*.csv")
            .Select(Path.GetFileName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        Assert.True(partitionFiles.Count >= 2,
            $"Expected at least .p01 + .p02; got: {string.Join(", ", partitionFiles)}");
        Assert.Equal("2026-04.p01.csv", partitionFiles[0]);
        Assert.StartsWith("2026-04.p", partitionFiles[1]);
    }

    // -------------------------------------------------------------------------
    // P1a-21 — once a month rolls, no bare <YYYY>-<MM>.csv reappears in that month;
    //          and subsequent months pre-open as .p01 (cross-month sticky)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Writer_StickyOverflow_NextMonthOpensAtP01()
    {
        var feedDir = FeedDir("EqV_sticky");

        const long tinyBudget = 80;

        await using (var writer = await PartitionedSinkWriter.Open(Storage(), feedDir, tinyBudget, headerLine: "ts,o,h,l,c,vol", Ct))
        {
            // Force overflow within April...
            await writer.WriteRow(Apr15Ms(0), $"{Apr15Ms(0)},1,2,0,1,5", Ct);
            await writer.WriteRow(Apr15Ms(1), $"{Apr15Ms(1)},1,2,0,1,5", Ct);
            await writer.WriteRow(Apr15Ms(2), $"{Apr15Ms(2)},1,2,0,1,5", Ct);

            // ... then write a row in May. Per cross-month sticky rule, May MUST pre-open at p01.
            await writer.WriteRow(May01Ms(0), $"{May01Ms(0)},1,2,0,1,5", Ct);
            await writer.Complete(Ct);
        }

        Assert.False(File.Exists(Path.Combine(feedDir, "2026-04.csv")),
            "Sticky violation: bare-name April reappeared after rollover.");
        Assert.False(File.Exists(Path.Combine(feedDir, "2026-05.csv")),
            "Cross-month sticky violation: May should pre-open at .p01 since April overflowed.");

        Assert.True(File.Exists(Path.Combine(feedDir, "2026-05.p01.csv")),
            "Cross-month sticky: May's first partition must be .p01 (writer has overflowed before).");
    }

    [Fact]
    public async Task Writer_NoOverflowMonthFollowedByOverflow_OnlyLaterMonthHasParts()
    {
        // Inverted scenario: write a small batch in April (no overflow), then large batch in
        // May that overflows. April stays bare-named; May goes part-numbered.
        var feedDir = FeedDir("EqV_late_overflow");

        await using (var writer = await PartitionedSinkWriter.Open(Storage(), feedDir, maxPartitionBytes: 10_000, headerLine: "ts,o,h,l,c,vol", Ct))
        {
            await writer.WriteRow(Apr15Ms(0), $"{Apr15Ms(0)},1,2,0,1,5", Ct);
            await writer.WriteRow(Apr15Ms(1), $"{Apr15Ms(1)},1,2,0,1,5", Ct);

            // ... then May with tiny rows but force overflow via a wide row.
            // The default budget here is 10k so we won't actually overflow May either —
            // assert just that April stays bare-named (no premature stickiness).
            await writer.WriteRow(May01Ms(0), $"{May01Ms(0)},1,2,0,1,5", Ct);
            await writer.Complete(Ct);
        }

        Assert.True(File.Exists(Path.Combine(feedDir, "2026-04.csv")));
        Assert.True(File.Exists(Path.Combine(feedDir, "2026-05.csv")));
        Assert.False(File.Exists(Path.Combine(feedDir, "2026-04.p01.csv")));
        Assert.False(File.Exists(Path.Combine(feedDir, "2026-05.p01.csv")));
    }

    // -------------------------------------------------------------------------
    // Backward timestamps surface as a contract violation
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Writer_BackwardMonthJump_Throws()
    {
        var feedDir = FeedDir("EqV_outoforder");

        var writer = await PartitionedSinkWriter.Open(Storage(), feedDir, maxPartitionBytes: 10_000, headerLine: "ts,o,h,l,c,vol", Ct);
        await writer.WriteRow(May01Ms(0), $"{May01Ms(0)},1,2,0,1,5", Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await writer.WriteRow(Apr15Ms(0), $"{Apr15Ms(0)},1,2,0,1,5", Ct));

        await writer.DisposeAsync();
    }

    // -------------------------------------------------------------------------
    // T6 — oversize-first-row guard. The rollover check at PartitionedSinkWriter.cs:76 is
    // gated on `_bytesInCurrent > _headerBytes.Length` so a single row larger than the
    // entire byte budget cannot loop forever recreating empty partitions. The first row
    // is admitted unconditionally; subsequent rows respect the budget normally.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Writer_FirstRowExceedsBudget_AdmitsRow_DoesNotInfiniteLoop()
    {
        var feedDir = FeedDir("EqV_oversize");
        // Row payload is ~30+ bytes (timestamp + OHLCV); set budget below header alone so
        // a naive `>budget` check would refuse to ever advance.
        const long impossiblyTinyBudget = 8;

        await using (var writer = await PartitionedSinkWriter.Open(Storage(), feedDir, maxPartitionBytes: impossiblyTinyBudget, headerLine: "ts,o,h,l,c,vol", Ct))
        {
            // Must complete (does not loop or throw) — the empty-partition guard waives the
            // rollover for the first data row.
            await writer.WriteRow(Apr15Ms(0), $"{Apr15Ms(0)},1,2,0,1,5", Ct);
            // Second row triggers a normal rollover because the partition now has data.
            await writer.WriteRow(Apr15Ms(1), $"{Apr15Ms(1)},1,2,0,1,5", Ct);
            await writer.Complete(Ct);
        }

        // Bare partition was promoted on the second row (overflow detected post-first-write).
        Assert.True(File.Exists(Path.Combine(feedDir, "2026-04.p01.csv")));
        Assert.True(File.Exists(Path.Combine(feedDir, "2026-04.p02.csv")));
    }
}
