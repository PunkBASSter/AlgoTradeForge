using System.Text;
using System.Text.Json;
using AlgoTradeForge.Application.Events;
using AlgoTradeForge.Domain.Events;
using AlgoTradeForge.Infrastructure.Events;
using AlgoTradeForge.Infrastructure.IO;
using Xunit;

namespace AlgoTradeForge.Infrastructure.Tests.Events;

public class JsonlFileSinkTests : IDisposable
{
    private readonly string _testRoot;
    private readonly FileStorage _fs = new();

    public JsonlFileSinkTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"JsonlSinkTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
    }

    private RunIdentity MakeIdentity() => new()
    {
        StrategyName = "TestStrat",
        StrategyVersion = "0",
        AssetName = "BTCUSDT",
        StartTime = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
        EndTime = new DateTimeOffset(2024, 12, 31, 0, 0, 0, TimeSpan.Zero),
        InitialCash = 100_000,
        RunMode = ExportMode.Backtest,
        RunTimestamp = DateTimeOffset.UtcNow,
    };

    private EventLogStorageOptions MakeOptions() => new() { Root = _testRoot };

    [Fact]
    public void Constructor_CreatesRunDirectory()
    {
        var identity = MakeIdentity();

        using var sink = new JsonlFileSink(identity, MakeOptions(), _fs);

        Assert.True(Directory.Exists(sink.RunFolderPath));
    }

    [Fact]
    public void Write_ProducesValidJsonlLines()
    {
        var identity = MakeIdentity();
        var sink = new JsonlFileSink(identity, MakeOptions(), _fs);

        var line1 = Encoding.UTF8.GetBytes("""{"sq":1,"_t":"bar"}""");
        var line2 = Encoding.UTF8.GetBytes("""{"sq":2,"_t":"bar"}""");
        sink.Write(line1);
        sink.Write(line2);
        sink.Dispose();

        var path = Path.Combine(sink.RunFolderPath, "events.jsonl");
        var lines = _fs.ReadAllLines(path);
        Assert.Equal(2, lines.Length);

        var doc1 = JsonDocument.Parse(lines[0]);
        Assert.Equal(1, doc1.RootElement.GetProperty("sq").GetInt64());
        var doc2 = JsonDocument.Parse(lines[1]);
        Assert.Equal(2, doc2.RootElement.GetProperty("sq").GetInt64());
    }

    [Fact]
    public void Write_SequenceOrdering_Preserved()
    {
        var identity = MakeIdentity();
        var sink = new JsonlFileSink(identity, MakeOptions(), _fs);

        for (var i = 1; i <= 5; i++)
            sink.Write(Encoding.UTF8.GetBytes($"{{\"sq\":{i}}}"));

        sink.Dispose();

        var path = Path.Combine(sink.RunFolderPath, "events.jsonl");
        var lines = _fs.ReadAllLines(path);
        Assert.Equal(5, lines.Length);

        for (var i = 0; i < 5; i++)
        {
            var doc = JsonDocument.Parse(lines[i]);
            Assert.Equal(i + 1, doc.RootElement.GetProperty("sq").GetInt64());
        }
    }

    [Fact]
    public void ConcurrentRead_WhileWriting_Succeeds()
    {
        var identity = MakeIdentity();
        using var sink = new JsonlFileSink(identity, MakeOptions(), _fs);

        sink.Write(Encoding.UTF8.GetBytes("""{"sq":1}"""));

        // Read with FileShare.ReadWrite so we can read while sink is writing
        var path = Path.Combine(sink.RunFolderPath, "events.jsonl");
        var lines = _fs.ReadAllLines(path);
        Assert.Single(lines);
    }

    [Fact]
    public void WriteMeta_CreatesMetaJson()
    {
        var identity = MakeIdentity();
        using var sink = new JsonlFileSink(identity, MakeOptions(), _fs);

        var summary = new RunSummary(1000, 105_000, 42, TimeSpan.FromSeconds(3.5));
        sink.WriteMeta(summary);

        var metaPath = Path.Combine(sink.RunFolderPath, "meta.json");
        Assert.True(File.Exists(metaPath));

        var json = _fs.ReadAllText(metaPath);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("TestStrat", root.GetProperty("strategyName").GetString());
        Assert.Equal("BTCUSDT", root.GetProperty("assetName").GetString());
        Assert.Equal("backtest", root.GetProperty("runMode").GetString());
        Assert.Equal(1000, root.GetProperty("totalBarsProcessed").GetInt32());
        Assert.Equal(105_000, root.GetProperty("finalEquity").GetInt64());
        Assert.Equal(42, root.GetProperty("totalFills").GetInt32());
    }

    [Fact]
    public void MetaJson_NotCreated_UntilWriteMetaCalled()
    {
        var identity = MakeIdentity();
        using var sink = new JsonlFileSink(identity, MakeOptions(), _fs);

        sink.Write(Encoding.UTF8.GetBytes("""{"sq":1}"""));

        var metaPath = Path.Combine(sink.RunFolderPath, "meta.json");
        Assert.False(File.Exists(metaPath));
    }

    [Fact]
    public void Dispose_ClosesFileHandle()
    {
        var identity = MakeIdentity();
        var sink = new JsonlFileSink(identity, MakeOptions(), _fs);

        sink.Write(Encoding.UTF8.GetBytes("""{"sq":1}"""));
        var path = Path.Combine(sink.RunFolderPath, "events.jsonl");

        sink.Dispose();

        // After dispose, we should be able to open the file exclusively
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(fs.Length > 0);
    }

    [Fact]
    public void Integration_ThroughEventBus_WritesEvents()
    {
        var identity = MakeIdentity();
        using var sink = new JsonlFileSink(identity, MakeOptions(), _fs);
        var bus = new EventBus(ExportMode.Backtest, [sink]);

        var ts = new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.Zero);
        bus.Emit(new WarningEvent(ts, "engine", "test warning"));

        // Read with FileShare.ReadWrite so we can read while sink is writing
        var path = Path.Combine(sink.RunFolderPath, "events.jsonl");
        var lines = _fs.ReadAllLines(path);
        Assert.Single(lines);

        var doc = JsonDocument.Parse(lines[0]);
        Assert.Equal("warn", doc.RootElement.GetProperty("_t").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("sq").GetInt64());
    }

    [Fact]
    public void Factory_CreatesSinkWithCorrectPath()
    {
        var options = Microsoft.Extensions.Options.Options.Create(MakeOptions());
        var factory = new JsonlRunSinkFactory(options, _fs);
        var identity = MakeIdentity();

        using var sink = factory.Create(identity);

        Assert.True(Directory.Exists(sink.RunFolderPath));
        Assert.StartsWith(_testRoot, sink.RunFolderPath);
    }
}
