using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Application.Aggregation;
using AlgoTradeForge.HistoryLoader.Infrastructure.Storage;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Aggregation;

/// <summary>
/// P5-12 — end-to-end Range/Renko pipeline tests.
/// Drives <see cref="AggregationPipeline.Run"/> with synthetic tick sources and verifies
/// manifest fields (type code, threshold unit, sidecar=null, imbalance_reconstruction_method=null)
/// plus the on-disk partition output.
/// </summary>
public sealed class AggregationPipeline_RangeRenkoTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), $"AggregationPipeline_RangeRenkoTests_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string AssetDir(string asset) => Path.Combine(_tempDir, "binance", asset);
    private string TicksDir(string asset) => Path.Combine(AssetDir(asset), "ticks");

    private void WriteTicks(string asset, string day,
        params (long ts, long price, long qty, int isBuyerMaker, long aggId)[] rows)
    {
        var dir = TicksDir(asset);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{day}.csv");
        using var sw = new StreamWriter(path);
        sw.WriteLine("ts,price,qty,is_buyer_maker,agg_id");
        foreach (var r in rows)
            sw.WriteLine($"{r.ts},{r.price},{r.qty},{r.isBuyerMaker},{r.aggId}");
    }

    private static long Ts(int year, int month, int day, int hour = 0, int minute = 0, int second = 0, int ms = 0) =>
        new DateTimeOffset(year, month, day, hour, minute, second, ms, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private AggregationJob BuildJob(string asset, string typeCode, long thresholdScaled, decimal thresholdAbs)
    {
        var scale = new ScaleContext(tickSize: 0.01m, quantityStepSize: 0.0001m);
        return new AggregationJob(
            JobId: $"{typeCode}-job-test",
            Source: new DataFeedDescriptor(_tempDir, "binance", asset, "ticks", DataFeedKind.Tick),
            AssetDir: AssetDir(asset),
            OutcomeFeedId: $"{typeCode}_ticks_{thresholdAbs}",
            TypeCode: typeCode,
            ThresholdAbsolute: thresholdAbs,
            ThresholdScaled: thresholdScaled,
            ThresholdUnit: "price",
            ThresholdInputMode: "absolute",
            ThresholdConvenienceInput: null,
            SourceScale: scale,
            AccumulatorScale: scale,
            MaxPartitionSizeMB: 1,
            ToolVersion: "test-1.0");
    }

    private static AggregationPipeline NewPipeline() => new(
        new PartitionedSourceReader(),
        new FeedSchemaManager(),
        new OverwritePathWriter(),
        TimeProvider.System);

    // -----------------------------------------------------------------
    // Range
    // -----------------------------------------------------------------

    [Fact]
    public void Run_RangeFromTicks_EmitsBars_ManifestUnitIsPrice_SidecarNull()
    {
        const string asset = "BTCUSDT_perp";
        // Walking price tick stream; threshold = 50 ticks (= $0.50 with tickSize=0.01).
        long t0 = Ts(2024, 4, 15, 12);
        var rows = new[]
        {
            // Bar 1: 100 → 110 → 95 → 155 (max range = 60 at the last tick → emits).
            (t0,        100L, 5L, 0, 1L),
            (t0 + 100,  110L, 4L, 0, 2L),
            (t0 + 200,  95L,  3L, 1, 3L),
            (t0 + 300,  155L, 6L, 0, 4L),    // range = 60 ≥ 50 → emit
            // Bar 2: 150 → 200 (range = 50 at the second tick → emits).
            (t0 + 400,  150L, 5L, 0, 5L),
            (t0 + 500,  200L, 4L, 0, 6L),    // range = 50 ≥ 50 → emit
            // Bar 3: 130 → 205 (range = 75 at the second tick → emits).
            (t0 + 600,  130L, 3L, 1, 7L),
            (t0 + 700,  205L, 5L, 0, 8L),    // range = 75 ≥ 50 → emit
        };
        WriteTicks(asset, "2024-04-15", rows);

        var pipeline = NewPipeline();
        var result = pipeline.Run(
            BuildJob(asset, "Range", thresholdScaled: 50, thresholdAbs: 50m),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(3, result.BarCount);
        Assert.Null(result.SidecarFeedId);

        // Manifest assertions.
        var manifest = new FeedSchemaManager().Load(AssetDir(asset));
        Assert.NotNull(manifest);
        var entry = Assert.Contains("Range_ticks_50", manifest!.Feeds);
        Assert.Equal("OHLCV_AltBar", entry.Kind);
        Assert.NotNull(entry.Type);
        Assert.Equal("Range", entry.Type!.Code);
        Assert.NotNull(entry.Threshold);
        Assert.Equal("price", entry.Threshold!.Unit);
        Assert.Equal("absolute", entry.Threshold.InputMode);
        Assert.Null(entry.Sidecar);
        Assert.NotNull(entry.Fidelity);
        Assert.Null(entry.Fidelity!.ImbalanceReconstructionMethod);    // non-EqIV stays null

        // Partition CSV exists and starts with the header.
        var feedDir = Path.Combine(AssetDir(asset), "aggregated", "Range_ticks_50");
        Assert.True(Directory.Exists(feedDir));
        var partitions = Directory.EnumerateFiles(feedDir, "*.csv").ToArray();
        Assert.NotEmpty(partitions);
        var firstLine = File.ReadAllLines(partitions[0]).First();
        Assert.Equal("ts,o,h,l,c,vol", firstLine);
    }

    // -----------------------------------------------------------------
    // Renko
    // -----------------------------------------------------------------

    [Fact]
    public void Run_RenkoFromTicks_EmitsBars_ManifestUnitIsPrice_SidecarNull()
    {
        const string asset = "ETHUSDT_perp";
        // Brick size = 50 (= $0.50). Walking price triggers a multi-brick chain.
        long t0 = Ts(2024, 4, 15, 12);
        var rows = new[]
        {
            (t0,        100L, 7L, 0, 1L),    // seed; pending = 7
            (t0 + 100,  150L, 9L, 0, 2L),    // delta = 50 → 1 brick (vol = 7 + 9 = 16)
            (t0 + 200,  300L, 30L, 0, 3L),   // delta = 150 → 3 bricks
            (t0 + 300,  300L, 1L, 0, 4L),    // delta = 0 → no emit; pending = 1
        };
        WriteTicks(asset, "2024-04-15", rows);

        var pipeline = NewPipeline();
        var result = pipeline.Run(
            BuildJob(asset, "Renko", thresholdScaled: 50, thresholdAbs: 50m),
            ct: TestContext.Current.CancellationToken);

        // 1 + 3 = 4 bricks. The trailing partial (pending=1) doesn't emit at finalize.
        Assert.Equal(4, result.BarCount);
        Assert.Null(result.SidecarFeedId);

        var manifest = new FeedSchemaManager().Load(AssetDir(asset));
        Assert.NotNull(manifest);
        var entry = Assert.Contains("Renko_ticks_50", manifest!.Feeds);
        Assert.Equal("OHLCV_AltBar", entry.Kind);
        Assert.Equal("Renko", entry.Type!.Code);
        Assert.Equal("price", entry.Threshold!.Unit);
        Assert.Null(entry.Sidecar);
        Assert.Null(entry.Fidelity!.ImbalanceReconstructionMethod);

        // Output CSV: 4 rows of bricks, each $50 wide. Verify volume conservation.
        var feedDir = Path.Combine(AssetDir(asset), "aggregated", "Renko_ticks_50");
        var partitions = Directory.EnumerateFiles(feedDir, "*.csv").ToArray();
        Assert.NotEmpty(partitions);
        var lines = File.ReadAllLines(partitions[0]);
        Assert.Equal(5, lines.Length);   // 1 header + 4 bricks
        long totalVolFromBricks = 0;
        for (var i = 1; i < lines.Length; i++)
        {
            var cells = lines[i].Split(',');
            totalVolFromBricks += long.Parse(cells[5]);
        }
        // Σ brick.vol = pending(1) + tick volumes consumed by emits = 7 + 9 + 30 = 46.
        // Tick 4 (qty=1) didn't emit, stays as pending — not in any brick.
        Assert.Equal(46, totalVolFromBricks);
    }

    [Theory]
    [InlineData("Range")]
    [InlineData("Renko")]
    public void Run_NonTickSource_ThrowsDefenseInDepthGuard(string typeCode)
    {
        // ADR D1 — EligibilityRules normally blocks this, but the pipeline guard catches
        // private-API callers that bypass eligibility (e.g. integration shims, future bugs).
        const string asset = "BTCUSDT_perp";
        Directory.CreateDirectory(Path.Combine(AssetDir(asset), "candles"));

        var scale = new ScaleContext(tickSize: 0.01m, quantityStepSize: 0.0001m);
        var job = new AggregationJob(
            JobId: $"{typeCode}-guard-test",
            Source: new DataFeedDescriptor(_tempDir, "binance", asset, "candles", DataFeedKind.TimeBar),
            AssetDir: AssetDir(asset),
            OutcomeFeedId: $"{typeCode}_1m_50",
            TypeCode: typeCode,
            ThresholdAbsolute: 50m,
            ThresholdScaled: 50,
            ThresholdUnit: "price",
            ThresholdInputMode: "absolute",
            ThresholdConvenienceInput: null,
            SourceScale: scale,
            AccumulatorScale: scale,
            MaxPartitionSizeMB: 1,
            ToolVersion: "test-1.0");

        var pipeline = NewPipeline();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            pipeline.Run(job, ct: TestContext.Current.CancellationToken));
        Assert.Contains(typeCode, ex.Message);
        Assert.Contains("Tick source", ex.Message);
    }

    [Fact]
    public void Run_RenkoMultiBrick_OutputTimestampsStrictlyMonotonic()
    {
        const string asset = "SOLUSDT_perp";
        // One tick triggers a 4-brick chain — output ts must bump.
        long t0 = Ts(2024, 4, 15, 12);
        WriteTicks(asset, "2024-04-15",
            (t0,        1000L, 0L, 0, 1L),    // seed
            (t0 + 100,  1200L, 40L, 0, 2L));  // delta = 200 → 4 bricks (brick=50)

        var pipeline = NewPipeline();
        var result = pipeline.Run(
            BuildJob(asset, "Renko", thresholdScaled: 50, thresholdAbs: 50m),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(4, result.BarCount);

        var feedDir = Path.Combine(AssetDir(asset), "aggregated", "Renko_ticks_50");
        var partitions = Directory.EnumerateFiles(feedDir, "*.csv").ToArray();
        var lines = File.ReadAllLines(partitions[0]);
        var timestamps = lines.Skip(1).Select(l => long.Parse(l.Split(',')[0])).ToArray();

        // Strictly monotonic across all 4 bricks.
        for (var i = 1; i < timestamps.Length; i++)
            Assert.True(timestamps[i] > timestamps[i - 1],
                $"ts[{i}]={timestamps[i]} should be > ts[{i - 1}]={timestamps[i - 1]}");
    }
}
