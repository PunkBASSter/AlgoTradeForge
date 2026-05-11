using System.Globalization;
using System.Text;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Infrastructure.Storage;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Storage;

/// <summary>
/// Covers TRD §3.5 / P2a-3: tail-of-CSV resume protocol with torn-write recovery and
/// agg_id dedup across day boundaries.
/// </summary>
public sealed class DailyTickCsvWriterTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _ticksDir;
    private readonly List<DailyTickCsvWriter> _writers = new();

    public DailyTickCsvWriterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"DailyTickCsvWriterTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _ticksDir = Path.Combine(_tempDir, FeedNames.Ticks);
    }

    public void Dispose()
    {
        // M1: writers cache an open StreamWriter per active day. Dispose them before
        // recursive-delete or Windows file-share rules will trigger IOException.
        foreach (var w in _writers)
            w.Dispose();

        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // 2024-03-15 12:00:00 UTC
    private static readonly long Ts20240315Noon =
        new DateTimeOffset(2024, 3, 15, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private static FeedRecord Tick(long ts, double price, double qty, bool isBuyerMaker, long aggId) =>
        new(ts, [price, qty, isBuyerMaker ? 1.0 : 0.0, aggId]);

    private DailyTickCsvWriter NewWriter()
    {
        var w = new DailyTickCsvWriter();
        _writers.Add(w);
        return w;
    }

    /// <summary>
    /// Reads a CSV file while the writer's cached handle may still be open. The writer
    /// holds <c>FileAccess.Write + FileShare.Read</c>; <c>File.ReadAllLines</c>
    /// requests <c>FileShare.Read</c> only, which fails because the existing write handle
    /// requires the new opener to also permit <c>Write</c>. This helper opens with
    /// <c>FileShare.ReadWrite</c> so the read coexists with the cached handle.
    /// </summary>
    private static string[] ReadCoexistingLines(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        var lines = new List<string>();
        string? line;
        while ((line = sr.ReadLine()) is not null)
            lines.Add(line);
        return lines.ToArray();
    }

    private static byte[] ReadCoexistingBytes(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var bytes = new byte[fs.Length];
        fs.ReadExactly(bytes);
        return bytes;
    }

    // -------------------------------------------------------------------------
    // 1. Write_NewFile_CreatesWithCorrectHeaderAndRow
    // -------------------------------------------------------------------------

    [Fact]
    public void Write_NewFile_CreatesWithCorrectHeaderAndRow()
    {
        var writer = NewWriter();
        writer.Write(_tempDir, Tick(Ts20240315Noon, 50000.5, 0.123, isBuyerMaker: false, aggId: 100));

        var path = Path.Combine(_ticksDir, "2024-03-15.csv");
        Assert.True(File.Exists(path));

        var lines = ReadCoexistingLines(path);
        Assert.Equal("ts,price,qty,is_buyer_maker,agg_id", lines[0]);
        Assert.Equal($"{Ts20240315Noon},50000.5,0.123,0,100", lines[1]);
    }

    // -------------------------------------------------------------------------
    // 2. Write_DedupsByAggId
    // -------------------------------------------------------------------------

    [Fact]
    public void Write_DedupsByAggId_DropsRepeatsRegardlessOfTs()
    {
        var writer = NewWriter();

        // Three records with the same agg_id — the latter two must be dropped even if ts differs.
        writer.Write(_tempDir, Tick(Ts20240315Noon, 50000, 1, false, aggId: 100));
        writer.Write(_tempDir, Tick(Ts20240315Noon + 5, 50001, 2, true, aggId: 100));   // dup id
        writer.Write(_tempDir, Tick(Ts20240315Noon + 10, 50002, 3, false, aggId: 100)); // dup id

        var path = Path.Combine(_ticksDir, "2024-03-15.csv");
        var lines = ReadCoexistingLines(path);
        Assert.Equal(2, lines.Length); // header + 1 row
    }

    // -------------------------------------------------------------------------
    // 3. ResumeFrom_NoFiles_ReturnsNull
    // -------------------------------------------------------------------------

    [Fact]
    public void ResumeFrom_NoFiles_ReturnsNull()
    {
        var writer = NewWriter();
        Assert.Null(writer.ResumeFrom(_tempDir));
    }

    // -------------------------------------------------------------------------
    // 4. ResumeFrom_HeaderOnlyFile_ReturnsNull
    // -------------------------------------------------------------------------

    [Fact]
    public void ResumeFrom_HeaderOnlyFile_ReturnsNull()
    {
        Directory.CreateDirectory(_ticksDir);
        File.WriteAllText(Path.Combine(_ticksDir, "2024-03-15.csv"),
            "ts,price,qty,is_buyer_maker,agg_id\n");

        var writer = NewWriter();
        Assert.Null(writer.ResumeFrom(_tempDir));
    }

    // -------------------------------------------------------------------------
    // 5. ResumeFrom_CleanFile_ReturnsLastRow
    // -------------------------------------------------------------------------

    [Fact]
    public void ResumeFrom_CleanFile_ReturnsLastRow()
    {
        var writer = NewWriter();
        writer.Write(_tempDir, Tick(Ts20240315Noon, 50000, 1, false, aggId: 100));
        writer.Write(_tempDir, Tick(Ts20240315Noon + 1000, 50100, 1, true, aggId: 101));
        writer.Write(_tempDir, Tick(Ts20240315Noon + 2000, 50200, 1, false, aggId: 102));

        // Simulate process restart: dispose the first writer's cached handle, then create
        // a fresh instance with empty in-memory state.
        writer.Dispose();

        var freshWriter = NewWriter();
        var resume = freshWriter.ResumeFrom(_tempDir);

        Assert.NotNull(resume);
        Assert.Equal(102, resume!.Value.LastAggId);
        Assert.Equal(Ts20240315Noon + 2000, resume.Value.LastTsMs);
    }

    // -------------------------------------------------------------------------
    // 6. ResumeFrom_TornLastRow_TruncatesAndReturnsPriorRow (P2a-3 core)
    // -------------------------------------------------------------------------

    [Fact]
    public void ResumeFrom_TornLastRow_TruncatesAndReturnsPriorRow()
    {
        var writer = NewWriter();
        writer.Write(_tempDir, Tick(Ts20240315Noon, 50000, 1, false, aggId: 100));
        writer.Write(_tempDir, Tick(Ts20240315Noon + 1000, 50100, 1, true, aggId: 101));

        var path = Path.Combine(_ticksDir, "2024-03-15.csv");

        // Simulate process kill: release the cached handle so File.AppendAllText below
        // (and the freshWriter's ResumeFrom) can open the file.
        writer.Dispose();

        // Append a torn row: process killed mid-line, no trailing newline.
        File.AppendAllText(path, $"{Ts20240315Noon + 2000},50200,1,0,", Encoding.UTF8);

        var lengthBeforeRepair = new FileInfo(path).Length;

        var freshWriter = NewWriter();
        var resume = freshWriter.ResumeFrom(_tempDir);

        Assert.NotNull(resume);
        Assert.Equal(101, resume!.Value.LastAggId);
        Assert.Equal(Ts20240315Noon + 1000, resume.Value.LastTsMs);

        // File should be truncated — torn bytes gone, last byte is '\n'.
        var lengthAfterRepair = new FileInfo(path).Length;
        Assert.True(lengthAfterRepair < lengthBeforeRepair,
            $"expected file to shrink: before={lengthBeforeRepair}, after={lengthAfterRepair}");
        var lastByte = ReadCoexistingBytes(path)[^1];
        Assert.Equal((byte)'\n', lastByte);

        // After repair, we must be able to append cleanly with no orphan bytes from the torn row.
        freshWriter.Write(_tempDir, Tick(Ts20240315Noon + 3000, 50300, 1, false, aggId: 102));
        var lines = ReadCoexistingLines(path);
        // header + agg_id 100, 101, 102 — no agg_id 200 ghost
        Assert.Equal(4, lines.Length);
        Assert.EndsWith(",102", lines[^1]);
    }

    // -------------------------------------------------------------------------
    // 7. ResumeFrom_AfterRepair_DedupRejectsReplay (P2a-3 dedup arm)
    // -------------------------------------------------------------------------

    [Fact]
    public void Write_AfterResumeFromTornFile_DedupsReplayedAggIds()
    {
        // Simulates: crash mid-collection, restart, Binance returns the boundary id-range
        // including the last successfully-written id. The writer must drop the replays.
        var writer = NewWriter();
        for (int i = 100; i <= 110; i++)
            writer.Write(_tempDir, Tick(Ts20240315Noon + i, 50000 + i, 1, false, aggId: i));

        // Simulate process kill — release the cached handle.
        writer.Dispose();

        var path = Path.Combine(_ticksDir, "2024-03-15.csv");
        File.AppendAllText(path, "tornbytes...", Encoding.UTF8);

        var freshWriter = NewWriter();
        var resume = freshWriter.ResumeFrom(_tempDir);
        Assert.NotNull(resume);
        Assert.Equal(110, resume!.Value.LastAggId);

        // Now simulate Binance redelivery: aggIds [109, 110, 111, 112] (replay overlap).
        freshWriter.Write(_tempDir, Tick(Ts20240315Noon + 109, 50109, 1, false, aggId: 109));
        freshWriter.Write(_tempDir, Tick(Ts20240315Noon + 110, 50110, 1, false, aggId: 110));
        freshWriter.Write(_tempDir, Tick(Ts20240315Noon + 111, 50111, 1, false, aggId: 111));
        freshWriter.Write(_tempDir, Tick(Ts20240315Noon + 112, 50112, 1, false, aggId: 112));

        var lines = ReadCoexistingLines(path);
        // header + 11 (100..110) + 2 new (111, 112) = 14
        Assert.Equal(14, lines.Length);
        Assert.EndsWith(",112", lines[^1]);

        // Verify monotonic agg_ids — no repeats.
        var seenIds = new HashSet<long>();
        for (int i = 1; i < lines.Length; i++)
        {
            var parts = lines[i].Split(',');
            var id = long.Parse(parts[4], CultureInfo.InvariantCulture);
            Assert.True(seenIds.Add(id), $"duplicate agg_id {id} at line {i + 1}");
        }
    }

    // -------------------------------------------------------------------------
    // 8. Write_DayBoundary_RoutesToCorrectPartition (P2a-3 boundary arm)
    // -------------------------------------------------------------------------

    [Fact]
    public void Write_DayBoundary_RoutesToCorrectPartition_NoSkipNoDuplicate()
    {
        var writer = NewWriter();

        // Day 1: a few rows, with a torn last row.
        var day1Last = new DateTimeOffset(2024, 3, 15, 23, 59, 59, 999, TimeSpan.Zero).ToUnixTimeMilliseconds();
        writer.Write(_tempDir, Tick(day1Last - 2000, 50000, 1, false, aggId: 200));
        writer.Write(_tempDir, Tick(day1Last - 1000, 50001, 1, false, aggId: 201));

        // Simulate process kill — release the cached day-1 handle.
        writer.Dispose();

        var path1 = Path.Combine(_ticksDir, "2024-03-15.csv");
        File.AppendAllText(path1, $"{day1Last},50002,1,0,", Encoding.UTF8); // torn

        // Resume — should return id 201 (clean), and truncate the torn line.
        var freshWriter = NewWriter();
        var resume = freshWriter.ResumeFrom(_tempDir);
        Assert.NotNull(resume);
        Assert.Equal(201, resume!.Value.LastAggId);

        // Now Binance redelivers: id 202 lands on day-1 (the previously-torn row), 203 on day-2.
        freshWriter.Write(_tempDir, Tick(day1Last, 50002, 1, false, aggId: 202));
        var day2First = new DateTimeOffset(2024, 3, 16, 0, 0, 0, 1, TimeSpan.Zero).ToUnixTimeMilliseconds();
        freshWriter.Write(_tempDir, Tick(day2First, 50010, 1, false, aggId: 203));

        // Day 1 file must contain 200, 201, 202 (no skip of 202 across the boundary).
        var day1Lines = ReadCoexistingLines(path1);
        Assert.Equal(4, day1Lines.Length); // header + 3 rows
        Assert.EndsWith(",202", day1Lines[^1]);

        // Day 2 file must exist with id 203 only.
        var path2 = Path.Combine(_ticksDir, "2024-03-16.csv");
        var day2Lines = ReadCoexistingLines(path2);
        Assert.Equal(2, day2Lines.Length); // header + 1 row
        Assert.EndsWith(",203", day2Lines[^1]);
    }

    // -------------------------------------------------------------------------
    // 9. ResumeFrom_PicksLatestDayLexicographically
    // -------------------------------------------------------------------------

    [Fact]
    public void ResumeFrom_PicksLatestDay_WhenMultiplePartitionsExist()
    {
        Directory.CreateDirectory(_ticksDir);
        File.WriteAllText(Path.Combine(_ticksDir, "2024-03-14.csv"),
            "ts,price,qty,is_buyer_maker,agg_id\n1,100,1,0,50\n");
        File.WriteAllText(Path.Combine(_ticksDir, "2024-03-15.csv"),
            "ts,price,qty,is_buyer_maker,agg_id\n2,200,1,0,75\n");

        var writer = NewWriter();
        var resume = writer.ResumeFrom(_tempDir);
        Assert.NotNull(resume);
        Assert.Equal(75, resume!.Value.LastAggId);
        Assert.Equal(2, resume.Value.LastTsMs);
    }

    // -------------------------------------------------------------------------
    // 10. Write_InvalidValueCount_Throws
    // -------------------------------------------------------------------------

    [Fact]
    public void Write_InvalidValueCount_Throws()
    {
        var writer = NewWriter();
        var bad = new FeedRecord(Ts20240315Noon, [50000, 1.0]); // only 2 values

        Assert.Throws<ArgumentException>(() => writer.Write(_tempDir, bad));
    }

    // -------------------------------------------------------------------------
    // 11. Write_DayRollover_DisposesPreviousDayHandle (M1)
    // -------------------------------------------------------------------------

    [Fact]
    public void Write_DayRollover_FlushesAndDisposesPreviousDay()
    {
        var writer = NewWriter();

        // Day 1: a few rows.
        var day1Ts = new DateTimeOffset(2024, 3, 15, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        writer.Write(_tempDir, Tick(day1Ts,         50000, 1, false, aggId: 100));
        writer.Write(_tempDir, Tick(day1Ts + 1000,  50001, 1, false, aggId: 101));

        // Day 2: triggers a rollover — the day-1 cache handle must flush + close so day-1
        // bytes are visible to a concurrent reader (and to the next test step).
        var day2Ts = new DateTimeOffset(2024, 3, 16, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        writer.Write(_tempDir, Tick(day2Ts, 50100, 1, false, aggId: 200));

        var day1Path = Path.Combine(_ticksDir, "2024-03-15.csv");
        var day1Lines = ReadCoexistingLines(day1Path);
        Assert.Equal(3, day1Lines.Length);  // header + 2
        Assert.EndsWith(",101", day1Lines[^1]);

        // Day 2 cache is still held; flush ensures readers see the row even before Dispose.
        var day2Path = Path.Combine(_ticksDir, "2024-03-16.csv");
        var day2Lines = ReadCoexistingLines(day2Path);
        Assert.Equal(2, day2Lines.Length);  // header + 1
    }

    // -------------------------------------------------------------------------
    // 12. Dispose_FlushesAndReleasesCachedHandle (M1)
    // -------------------------------------------------------------------------

    [Fact]
    public void Dispose_ReleasesCachedHandle_FileBecomesExclusivelyOpenable()
    {
        var writer = NewWriter();
        writer.Write(_tempDir, Tick(Ts20240315Noon, 50000, 1, false, aggId: 100));

        var path = Path.Combine(_ticksDir, "2024-03-15.csv");

        writer.Dispose();

        // After Dispose, the file must be openable for exclusive write — proves the cached
        // handle was released (and not just flushed-but-still-open). FileShare.None throws
        // IOException if any other handle on the path exists.
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        Assert.True(exclusive.Length > 0);
    }

    // -------------------------------------------------------------------------
    // 13. Write_AfterResumeFromOnSameDay_DisposesCacheBeforeReadHandle (M1)
    // -------------------------------------------------------------------------

    [Fact]
    public void Write_AfterResumeFromOnSameDay_RecoversCleanly()
    {
        var writer = NewWriter();
        writer.Write(_tempDir, Tick(Ts20240315Noon,        50000, 1, false, aggId: 100));
        writer.Write(_tempDir, Tick(Ts20240315Noon + 1000, 50001, 1, true,  aggId: 101));

        // ResumeFrom on the same writer instance — previously this would fail on Windows
        // because the FileAccess.ReadWrite handle would clash with the held Append+Write
        // handle. M1 fix: dispose cache inside the gate first.
        var resume = writer.ResumeFrom(_tempDir);
        Assert.NotNull(resume);
        Assert.Equal(101, resume!.Value.LastAggId);

        // Subsequent write must reopen the cache and append cleanly.
        writer.Write(_tempDir, Tick(Ts20240315Noon + 2000, 50002, 1, false, aggId: 102));

        var path = Path.Combine(_ticksDir, "2024-03-15.csv");
        var lines = ReadCoexistingLines(path);
        Assert.Equal(4, lines.Length);  // header + 3 rows
        Assert.EndsWith(",102", lines[^1]);
    }

    // -------------------------------------------------------------------------
    // 14. Dedup_LruCapacity_BoundedAtMaxSize (M2)
    // -------------------------------------------------------------------------

    [Fact]
    public void Write_AcrossManyDays_DedupCacheBoundedByLru()
    {
        // Write to 12 distinct days. The LRU cap is 8 — the oldest 4 entries must fall out.
        // After eviction, a re-Write to an evicted day with a duplicate aggId would no longer
        // dedup against in-memory state — but the dedup contract still holds because (a) the
        // caller would have to call ResumeFrom (which reseeds the entry from disk), or
        // (b) Binance's monotonic aggId guarantees no future write would conflict anyway.
        var writer = NewWriter();
        var baseTs = new DateTimeOffset(2024, 3, 1, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        for (int day = 0; day < 12; day++)
        {
            long ts = baseTs + day * TimeSpan.FromDays(1).Ticks / TimeSpan.TicksPerMillisecond;
            writer.Write(_tempDir, Tick(ts, 50000, 1, false, aggId: 100 + day));
        }

        // All 12 day-files exist on disk.
        var dayFiles = Directory.GetFiles(_ticksDir, "*.csv");
        Assert.Equal(12, dayFiles.Length);

        // Dispose so we can probe disk freely below.
        writer.Dispose();

        // ResumeFrom always reads from disk (LRU is repopulated from there) — proves the
        // disk truth is intact regardless of in-memory eviction.
        var resume = writer.ResumeFrom(_tempDir);
        Assert.NotNull(resume);
        // The latest day lex-sort'd is day=11 → aggId=111.
        Assert.Equal(111, resume!.Value.LastAggId);
    }
}
