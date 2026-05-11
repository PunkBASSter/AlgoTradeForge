using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Application.Aggregation;
using AlgoTradeForge.HistoryLoader.Infrastructure.Storage;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Aggregation;

/// <summary>
/// P1b-11 / P1b-13 / P1b-14 — full-pipeline tests with real <see cref="FeedSchemaManager"/>
/// and <see cref="OverwritePathWriter"/>. Exercises the read → accumulate → sink → promote →
/// manifest-write sequence end-to-end.
/// </summary>
public sealed class AggregationPipelineTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), $"AggregationPipelineTests_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string AssetDir(string asset) =>
        Path.Combine(_tempDir, "binance", asset);

    private string CandlesDir(string asset) =>
        Path.Combine(AssetDir(asset), "candles");

    private void WriteCandles(string asset, string month, string interval, params (long ts, long o, long h, long l, long c, long v)[] rows)
    {
        var dir = CandlesDir(asset);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{month}_{interval}.csv");
        using var sw = new StreamWriter(path);
        sw.WriteLine("ts,o,h,l,c,vol");
        foreach (var r in rows)
            sw.WriteLine($"{r.ts},{r.o},{r.h},{r.l},{r.c},{r.v}");
    }

    private static long Ts(int year, int month, int day, int hour = 0) =>
        new DateTimeOffset(year, month, day, hour, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private AggregationJob Job(string asset, string typeCode, long thresholdScaled, decimal thresholdAbs)
    {
        var scale = new ScaleContext(tickSize: 0.01m, quantityStepSize: 0.0001m);
        return new AggregationJob(
            JobId: "job-test",
            Source: new DataFeedDescriptor(_tempDir, "binance", asset, "1m", DataFeedKind.TimeBar),
            AssetDir: AssetDir(asset),
            OutcomeFeedId: $"{typeCode}_1m_{thresholdAbs}",
            TypeCode: typeCode,
            ThresholdAbsolute: thresholdAbs,
            ThresholdScaled: thresholdScaled,
            ThresholdUnit: "base_asset",
            ThresholdInputMode: "absolute",
            ThresholdConvenienceInput: null,
            SourceScale: scale,
            AccumulatorScale: scale,
            MaxPartitionSizeMB: 1,
            ToolVersion: "test-1.0");
    }

    [Fact]
    public void Run_EqV_EmitsExpectedBarsAndManifest()
    {
        const string asset = "BTCUSDT";
        // 6 records of vol=400 → 2 bars at threshold 1000 (each realized=1200, overshoot=20%).
        WriteCandles(asset, "2024-01", "1m",
            (Ts(2024, 1, 1, 0), 100, 110, 95, 105, 400),
            (Ts(2024, 1, 1, 1), 105, 115, 100, 110, 400),
            (Ts(2024, 1, 1, 2), 110, 120, 105, 118, 400),
            (Ts(2024, 1, 1, 3), 118, 125, 115, 122, 400),
            (Ts(2024, 1, 1, 4), 122, 130, 120, 128, 400),
            (Ts(2024, 1, 1, 5), 128, 135, 125, 132, 400));

        var pipeline = new AggregationPipeline(
            new PartitionedSourceReader(),
            new FeedSchemaManager(),
            new OverwritePathWriter(),
            TimeProvider.System);

        var result = pipeline.Run(
            Job(asset, "EqV", thresholdScaled: 1000, thresholdAbs: 1000m),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.BarCount);
        Assert.Equal(20d, result.ActualOvershootPct, 5);
        Assert.Equal(20d, result.MaxOvershootPct, 5);

        // Manifest entry is present and well-formed.
        var manifest = new FeedSchemaManager().Load(AssetDir(asset));
        Assert.NotNull(manifest);
        var entry = Assert.Contains("EqV_1m_1000", manifest!.Feeds);
        Assert.Equal("OHLCV_AltBar", entry.Kind);
        Assert.Equal("EqV", entry.Type!.Code);
        Assert.Equal("1m", entry.Source!.Feed);
        Assert.Equal(6L, entry.Source.RecordCount);
        Assert.Equal(1000m, entry.Threshold!.Value);
        Assert.Equal("absolute", entry.Threshold.InputMode);
        Assert.Equal(2L, entry.Build!.BarCount);
        Assert.Equal(20d, entry.Fidelity!.ActualOvershootPct!.Value, 5);

        // Promoted dir contains the produced partition; staging dir is gone.
        var feedDir = Path.Combine(AssetDir(asset), "aggregated", "EqV_1m_1000");
        Assert.True(Directory.Exists(feedDir));
        Assert.True(File.Exists(Path.Combine(feedDir, "2024-01.csv")));
        Assert.Empty(Directory.GetDirectories(feedDir, ".staging-*"));
    }

    [Fact]
    public void Run_NoSourceRecords_EmitsZeroBarsAndStillWritesManifest()
    {
        const string asset = "ETHUSDT";
        Directory.CreateDirectory(CandlesDir(asset));   // empty candles dir, no CSV files

        var pipeline = new AggregationPipeline(
            new PartitionedSourceReader(),
            new FeedSchemaManager(),
            new OverwritePathWriter(),
            TimeProvider.System);

        var result = pipeline.Run(
            Job(asset, "EqV", thresholdScaled: 1000, thresholdAbs: 1000m),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, result.BarCount);
        Assert.Equal(0d, result.ActualOvershootPct);
        Assert.Equal(0d, result.MaxOvershootPct);
        Assert.Null(result.FirstBarTs);
        Assert.Null(result.LastBarTs);

        var manifest = new FeedSchemaManager().Load(AssetDir(asset));
        Assert.NotNull(manifest);
        Assert.Contains("EqV_1m_1000", manifest!.Feeds);
    }

    [Fact]
    public void Run_OvershootStats_MatchAnalyticEstimate()
    {
        const string asset = "BTCUSDT2";
        // Constant-volume source with vol=500 (median=500), threshold=1000 → n_factor=2,
        // estimated_overshoot_pct = 100/(2*2) = 25%. Each bar takes 2 records → realized=1000,
        // actual_overshoot=0% (perfect fit). Maximum overshoot when records align perfectly.
        var rows = new (long ts, long o, long h, long l, long c, long v)[20];
        for (var i = 0; i < 20; i++)
            rows[i] = (Ts(2024, 1, 1, i), 100, 110, 95, 105, 500);
        WriteCandles(asset, "2024-01", "1m", rows);

        var pipeline = new AggregationPipeline(
            new PartitionedSourceReader(),
            new FeedSchemaManager(),
            new OverwritePathWriter(),
            TimeProvider.System);

        var result = pipeline.Run(
            Job(asset, "EqV", thresholdScaled: 1000, thresholdAbs: 1000m),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(10, result.BarCount);
        Assert.Equal(0d, result.ActualOvershootPct, 5);     // exact alignment
        Assert.Equal(500d, result.MedianSourceRecordValue, 5);
        Assert.Equal(2d, result.NFactor, 5);                  // 1000 / 500
        Assert.Equal(25d, result.EstimatedOvershootPct, 5);   // 100 / (2 * 2)
    }

    [Fact]
    public void Run_AssetSourceVolumesBoundedMemory_NoLargeAllocation()
    {
        // P1b-12 — peak heap stays bounded. Generate 1000 source records, observe the pipeline's
        // allocation delta is dominated by the volume-sample list (8 bytes/entry) plus partition-
        // writer buffers, not by the full record stream.
        const string asset = "BOUND";
        var rows = new (long ts, long o, long h, long l, long c, long v)[1000];
        for (var i = 0; i < 1000; i++)
            rows[i] = (Ts(2024, 1, 1) + i * 60_000, 100, 110, 95, 105, 50);
        WriteCandles(asset, "2024-01", "1m", rows);

        var pipeline = new AggregationPipeline(
            new PartitionedSourceReader(),
            new FeedSchemaManager(),
            new OverwritePathWriter(),
            TimeProvider.System);

        var allocBefore = GC.GetAllocatedBytesForCurrentThread();
        var result = pipeline.Run(
            Job(asset, "EqV", thresholdScaled: 1000, thresholdAbs: 1000m),
            ct: TestContext.Current.CancellationToken);
        var allocAfter = GC.GetAllocatedBytesForCurrentThread();
        var delta = allocAfter - allocBefore;

        // Loose bound: well under 1 MB for 1000 source records (volume samples = 8 KB; the rest
        // is StringBuilder reuse, partition writer buffer, and one manifest serialization).
        Assert.True(result.BarCount > 0);
        Assert.True(delta < 1_000_000, $"Allocation delta exceeded 1 MB: {delta} bytes.");
    }

    [Fact]
    public void Run_EmitsStartedAndCompleteProgressEvents()
    {
        const string asset = "PROGRESS";
        WriteCandles(asset, "2024-01", "1m",
            (Ts(2024, 1, 1, 0), 100, 110, 95, 105, 600),
            (Ts(2024, 1, 1, 1), 105, 115, 100, 110, 600));

        var pipeline = new AggregationPipeline(
            new PartitionedSourceReader(),
            new FeedSchemaManager(),
            new OverwritePathWriter(),
            TimeProvider.System);

        var events = new List<ProgressEvent>();
        pipeline.Run(
            Job(asset, "EqV", thresholdScaled: 1000, thresholdAbs: 1000m),
            events.Add,
            TestContext.Current.CancellationToken);

        Assert.IsType<ProgressEvent.Started>(events.First());
        Assert.IsType<ProgressEvent.Complete>(events.Last());
    }
}
