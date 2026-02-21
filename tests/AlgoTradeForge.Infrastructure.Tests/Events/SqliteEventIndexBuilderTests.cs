using System.Diagnostics;
using System.Text;
using AlgoTradeForge.Infrastructure.Events;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AlgoTradeForge.Infrastructure.Tests.Events;

public class SqliteEventIndexBuilderTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _runFolder;
    private readonly SqliteEventIndexBuilder _builder = new();

    public SqliteEventIndexBuilderTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"IndexBuilderTest_{Guid.NewGuid():N}");
        _runFolder = Path.Combine(_testRoot, "test_run");
        Directory.CreateDirectory(_runFolder);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
    }

    private void WriteSampleJsonl(int count = 10)
    {
        var sb = new StringBuilder();
        for (var i = 1; i <= count; i++)
        {
            var type = i % 3 == 0 ? "ord.fill" : i % 2 == 0 ? "ord.place" : "bar";
            sb.AppendLine($"{{\"ts\":\"2024-01-01T00:0{i:D2}:00+00:00\",\"sq\":{i},\"_t\":\"{type}\",\"src\":\"engine\",\"d\":{{\"v\":{i}}}}}");
        }
        File.WriteAllText(Path.Combine(_runFolder, "events.jsonl"), sb.ToString());
    }

    [Fact]
    public void Build_CreatesCorrectSchema_FromSampleJsonl()
    {
        WriteSampleJsonl();

        _builder.Build(_runFolder);

        var indexPath = Path.Combine(_runFolder, "index.sqlite");
        Assert.True(File.Exists(indexPath));

        using var conn = new SqliteConnection($"Data Source={indexPath};Mode=ReadOnly;Pooling=False");
        conn.Open();

        // Verify table exists with correct columns
        using var cols = conn.CreateCommand();
        cols.CommandText = "PRAGMA table_info(events)";
        using var reader = cols.ExecuteReader();
        var columnNames = new List<string>();
        while (reader.Read())
            columnNames.Add(reader.GetString(1));

        Assert.Contains("sq", columnNames);
        Assert.Contains("ts", columnNames);
        Assert.Contains("_t", columnNames);
        Assert.Contains("src", columnNames);
        Assert.Contains("raw", columnNames);

        // Verify 4 indexes exist
        using var idx = conn.CreateCommand();
        idx.CommandText = "SELECT name FROM sqlite_master WHERE type='index' AND tbl_name='events'";
        using var idxReader = idx.ExecuteReader();
        var indexNames = new List<string>();
        while (idxReader.Read())
            indexNames.Add(idxReader.GetString(0));

        Assert.Equal(4, indexNames.Count);
        Assert.Contains("ix_events_sq", indexNames);
        Assert.Contains("ix_events_ts", indexNames);
        Assert.Contains("ix_events_t", indexNames);
        Assert.Contains("ix_events_src", indexNames);
    }

    [Fact]
    public void Build_QueryByType_ReturnsCorrectEvents()
    {
        WriteSampleJsonl();

        _builder.Build(_runFolder);

        var indexPath = Path.Combine(_runFolder, "index.sqlite");
        using var conn = new SqliteConnection($"Data Source={indexPath};Mode=ReadOnly;Pooling=False");
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM events WHERE _t = 'bar'";
        var barCount = (long)cmd.ExecuteScalar()!;

        // In 10 events: indices 1,5,7 are bar (odd and not divisible by 3)
        // i=1 bar, i=2 ord.place, i=3 ord.fill, i=4 ord.place, i=5 bar, i=6 ord.fill, i=7 bar, i=8 ord.place, i=9 ord.fill, i=10 ord.place
        Assert.Equal(3, barCount);

        cmd.CommandText = "SELECT COUNT(*) FROM events WHERE _t = 'ord.fill'";
        var fillCount = (long)cmd.ExecuteScalar()!;
        Assert.Equal(3, fillCount);
    }

    [Fact]
    public void Build_QueryBySequenceRange_ReturnsOrderedEvents()
    {
        WriteSampleJsonl();

        _builder.Build(_runFolder);

        var indexPath = Path.Combine(_runFolder, "index.sqlite");
        using var conn = new SqliteConnection($"Data Source={indexPath};Mode=ReadOnly;Pooling=False");
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sq FROM events WHERE sq BETWEEN 5 AND 10 ORDER BY sq";
        using var reader = cmd.ExecuteReader();

        var sequences = new List<long>();
        while (reader.Read())
            sequences.Add(reader.GetInt64(0));

        Assert.Equal(6, sequences.Count);
        Assert.Equal([5, 6, 7, 8, 9, 10], sequences);
    }

    [Fact]
    public void Build_TransactionalOnFailure_NoPartialIndex()
    {
        // Write corrupt JSONL
        File.WriteAllText(
            Path.Combine(_runFolder, "events.jsonl"),
            """
            {"ts":"2024-01-01T00:00:00+00:00","sq":1,"_t":"bar","src":"engine","d":{}}
            NOT VALID JSON!!!
            """);

        Assert.ThrowsAny<Exception>(() => _builder.Build(_runFolder));

        var indexPath = Path.Combine(_runFolder, "index.sqlite");
        Assert.False(File.Exists(indexPath), "No index.sqlite should remain after failure");

        var tmpPath = indexPath + ".tmp";
        Assert.False(File.Exists(tmpPath), "No .tmp file should remain after failure");
    }

    [Fact]
    public void Rebuild_ReplacesExistingIndex()
    {
        WriteSampleJsonl(5);
        _builder.Build(_runFolder);

        var indexPath = Path.Combine(_runFolder, "index.sqlite");

        // Verify 5 rows
        using (var conn = new SqliteConnection($"Data Source={indexPath};Mode=ReadOnly;Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM events";
            Assert.Equal(5L, (long)cmd.ExecuteScalar()!);
        }

        // Rewrite with 3 events and rebuild
        WriteSampleJsonl(3);
        _builder.Rebuild(_runFolder);

        using (var conn = new SqliteConnection($"Data Source={indexPath};Mode=ReadOnly;Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM events";
            Assert.Equal(3L, (long)cmd.ExecuteScalar()!);
        }
    }

    [Fact]
    public void Build_SkipsIfAlreadyExists()
    {
        WriteSampleJsonl(5);
        _builder.Build(_runFolder);

        var indexPath = Path.Combine(_runFolder, "index.sqlite");
        var firstWriteTime = File.GetLastWriteTimeUtc(indexPath);

        // Small delay to ensure timestamp would differ
        Thread.Sleep(50);

        // Rewrite JSONL with different count — Build should be no-op
        WriteSampleJsonl(3);
        _builder.Build(_runFolder);

        Assert.Equal(firstWriteTime, File.GetLastWriteTimeUtc(indexPath));

        // Still has original 5 events
        using var conn = new SqliteConnection($"Data Source={indexPath};Mode=ReadOnly;Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM events";
        Assert.Equal(5L, (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public void Build_100KEvents_CompletesInUnder5Seconds()
    {
        var sb = new StringBuilder();
        for (var i = 1; i <= 100_000; i++)
            sb.AppendLine($"{{\"ts\":\"2024-01-01T00:00:00+00:00\",\"sq\":{i},\"_t\":\"bar\",\"src\":\"engine\",\"d\":{{\"v\":{i}}}}}");
        File.WriteAllText(Path.Combine(_runFolder, "events.jsonl"), sb.ToString());

        var sw = Stopwatch.StartNew();
        _builder.Build(_runFolder);
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"Build took {sw.Elapsed.TotalSeconds:F2}s, expected < 5s");

        var indexPath = Path.Combine(_runFolder, "index.sqlite");
        using var conn = new SqliteConnection($"Data Source={indexPath};Mode=ReadOnly;Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM events";
        Assert.Equal(100_000L, (long)cmd.ExecuteScalar()!);
    }
}
