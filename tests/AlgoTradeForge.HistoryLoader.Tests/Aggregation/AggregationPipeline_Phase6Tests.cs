using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Application.Aggregation;
using AlgoTradeForge.HistoryLoader.Infrastructure.Storage;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Aggregation;

/// <summary>
/// Phase 6 — pipeline tests for cancel cleanup (P6-11) and alt-bar re-aggregation (P6-13).
/// </summary>
public sealed class AggregationPipeline_Phase6Tests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), $"AggregationPipeline_Phase6Tests_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); }
            catch { /* best-effort */ }
        }
    }

    private string AssetDir(string asset) => Path.Combine(_tempDir, "binance", asset);

    private static AggregationPipeline NewPipeline() => new(
        new PartitionedSourceReader(),
        new FeedSchemaManager(),
        new OverwritePathWriter(),
        TimeProvider.System);

    private static long Ts(int year, int month, int day, int hour = 0, int minute = 0, int second = 0) =>
        new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.Zero).ToUnixTimeMilliseconds();

    // -------------------------------------------------------------------------
    // P6-11 — Cancellation cleanup
    // -------------------------------------------------------------------------

    [Fact]
    public void Run_CancelMidStream_DeletesStagingDir_NoManifestWrite()
    {
        const string asset = "BTCUSDT";
        var candlesDir = Path.Combine(AssetDir(asset), "candles");
        Directory.CreateDirectory(candlesDir);
        // 100 records — enough to start staging then cancel mid-stream.
        var t0 = Ts(2024, 6, 1, 12);
        using (var sw = new StreamWriter(Path.Combine(candlesDir, "2024-06_1m.csv")))
        {
            sw.WriteLine("ts,o,h,l,c,vol");
            for (var i = 0; i < 100; i++)
                sw.WriteLine($"{t0 + i * 60_000L},100,110,90,105,500");
        }

        var scale = new ScaleContext(tickSize: 0.01m, quantityStepSize: 0.0001m);
        var job = new AggregationJob(
            JobId: "cancel-test",
            Source: new DataFeedDescriptor(_tempDir, "binance", asset, "1m", DataFeedKind.TimeBar),
            AssetDir: AssetDir(asset),
            OutcomeFeedId: "EqV_1m_5000",
            TypeCode: "EqV",
            ThresholdAbsolute: 5_000m,
            ThresholdScaled: 50_000_000L,    // very large threshold so emits don't happen
            ThresholdUnit: "base_asset",
            ThresholdInputMode: "absolute",
            ThresholdConvenienceInput: null,
            SourceScale: scale,
            AccumulatorScale: scale,
            MaxPartitionSizeMB: 1,
            ToolVersion: "test-1.0");

        // Pre-cancelled token — pipeline throws on the first ct.ThrowIfCancellationRequested.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var pipeline = NewPipeline();
        Assert.Throws<OperationCanceledException>(() => pipeline.Run(job, ct: cts.Token));

        // Staging dir gone, no manifest written. The feed dir itself was created by the pipeline
        // (Directory.CreateDirectory at the top), but it should be empty.
        var feedDir = Path.Combine(AssetDir(asset), "aggregated", "EqV_1m_5000");
        if (Directory.Exists(feedDir))
        {
            Assert.Empty(Directory.GetDirectories(feedDir));   // no .staging-* survives
            Assert.Empty(Directory.GetFiles(feedDir));         // no promoted partitions
        }

        var manifestPath = Path.Combine(AssetDir(asset), "feeds.json");
        if (File.Exists(manifestPath))
        {
            // Even if a manifest existed before this run (none does here), it MUST not contain
            // an entry for our outcome feed-id.
            var content = File.ReadAllText(manifestPath);
            Assert.DoesNotContain("EqV_1m_5000", content);
        }
    }

    // -------------------------------------------------------------------------
    // P6-13 — Re-aggregation from alt-bar source (safe trio)
    // -------------------------------------------------------------------------

    [Fact]
    public void Run_EqV2000_FromEqV1000_ProducesHalfTheBars_WithDoubledVolume()
    {
        const string asset = "BTCUSDT";

        // Synthesize an existing EqV_1m_1000 feed: 10 alt-bars each with vol=1000 (the threshold).
        // Re-aggregating with EqV threshold 2000 should emit 5 output bars, each with vol=2000.
        var sourceFeedId = "EqV_1m_1000";
        var sourceDir = Path.Combine(AssetDir(asset), "aggregated", sourceFeedId);
        Directory.CreateDirectory(sourceDir);
        var t0 = Ts(2024, 6, 1, 12);
        using (var sw = new StreamWriter(Path.Combine(sourceDir, "2024-06.csv")))
        {
            sw.WriteLine("ts,o,h,l,c,vol");
            for (var i = 0; i < 10; i++)
                sw.WriteLine($"{t0 + i * 60_000L},100,110,90,105,1000");
        }

        var scale = new ScaleContext(tickSize: 0.01m, quantityStepSize: 0.0001m);
        var job = new AggregationJob(
            JobId: "reagg-test",
            Source: new DataFeedDescriptor(_tempDir, "binance", asset, sourceFeedId, DataFeedKind.AltBar),
            AssetDir: AssetDir(asset),
            OutcomeFeedId: "EqV_1m_2000",
            TypeCode: "EqV",
            ThresholdAbsolute: 2_000m,
            ThresholdScaled: 2_000L,
            ThresholdUnit: "base_asset",
            ThresholdInputMode: "absolute",
            ThresholdConvenienceInput: null,
            SourceScale: scale,
            AccumulatorScale: scale,
            MaxPartitionSizeMB: 1,
            ToolVersion: "test-1.0");

        var pipeline = NewPipeline();
        var result = pipeline.Run(job, ct: TestContext.Current.CancellationToken);

        // 10 source bars × 1000 vol each / 2000 threshold → 5 output bars.
        Assert.Equal(5, result.BarCount);

        // Each output bar's volume = sum of the two source bars (each contributing 1000).
        var feedDir = Path.Combine(AssetDir(asset), "aggregated", "EqV_1m_2000");
        var partitions = Directory.EnumerateFiles(feedDir, "*.csv").ToArray();
        Assert.NotEmpty(partitions);
        var lines = File.ReadAllLines(partitions[0]);
        Assert.Equal(6, lines.Length);   // 1 header + 5 bars
        for (var i = 1; i < lines.Length; i++)
        {
            var cells = lines[i].Split(',');
            Assert.Equal(2000, long.Parse(cells[5]));
        }

        // Manifest records the actual source feedId, not the underlying time-bar interval.
        var manifest = new FeedSchemaManager().Load(AssetDir(asset));
        Assert.NotNull(manifest);
        var entry = Assert.Contains("EqV_1m_2000", manifest!.Feeds);
        Assert.Equal(sourceFeedId, entry.Source!.Feed);
        Assert.Null(entry.Fidelity!.ImbalanceReconstructionMethod);   // non-EqIV stays null
    }

    [Fact]
    public void Run_EqT200_FromEqT100_ProducesHalfTheBars()
    {
        const string asset = "BTCUSDT";
        var sourceFeedId = "EqT_1m_100";
        var sourceDir = Path.Combine(AssetDir(asset), "aggregated", sourceFeedId);
        Directory.CreateDirectory(sourceDir);
        var t0 = Ts(2024, 6, 1, 12);
        using (var sw = new StreamWriter(Path.Combine(sourceDir, "2024-06.csv")))
        {
            sw.WriteLine("ts,o,h,l,c,vol");
            for (var i = 0; i < 8; i++)
                sw.WriteLine($"{t0 + i * 60_000L},100,110,90,105,500");
        }

        var scale = new ScaleContext(tickSize: 0.01m, quantityStepSize: 0.0001m);
        var job = new AggregationJob(
            JobId: "reagg-eqt-test",
            Source: new DataFeedDescriptor(_tempDir, "binance", asset, sourceFeedId, DataFeedKind.AltBar),
            AssetDir: AssetDir(asset),
            OutcomeFeedId: "EqT_1m_2",
            TypeCode: "EqT",
            ThresholdAbsolute: 2m,
            ThresholdScaled: 2L,             // EqT counts records — 2 source bars per output bar
            ThresholdUnit: "trades",
            ThresholdInputMode: "absolute",
            ThresholdConvenienceInput: null,
            SourceScale: scale,
            AccumulatorScale: scale,
            MaxPartitionSizeMB: 1,
            ToolVersion: "test-1.0");

        var result = NewPipeline().Run(job, ct: TestContext.Current.CancellationToken);

        Assert.Equal(4, result.BarCount);   // 8 source bars / 2 = 4
    }

    [Fact]
    public void Run_EqDFromEqD_PreservesQuoteVolumeAccumulation()
    {
        // EqD's per-record contribution is close × volume. With close=100, vol=1000 per source
        // bar, each contributes 100 × 1000 = 100_000 quote units. Threshold 200_000 → 2 source
        // bars per output bar. (Threshold is in quote_asset units; accumulator scale wraps it.)
        const string asset = "BTCUSDT";
        var sourceFeedId = "EqD_1m_100k";
        var sourceDir = Path.Combine(AssetDir(asset), "aggregated", sourceFeedId);
        Directory.CreateDirectory(sourceDir);
        var t0 = Ts(2024, 6, 1, 12);
        using (var sw = new StreamWriter(Path.Combine(sourceDir, "2024-06.csv")))
        {
            sw.WriteLine("ts,o,h,l,c,vol");
            for (var i = 0; i < 6; i++)
                sw.WriteLine($"{t0 + i * 60_000L},10000,10000,10000,10000,100");   // close*vol = 1_000_000 per bar
        }

        var scale = new ScaleContext(tickSize: 0.01m, quantityStepSize: 0.0001m);
        var job = new AggregationJob(
            JobId: "reagg-eqd-test",
            Source: new DataFeedDescriptor(_tempDir, "binance", asset, sourceFeedId, DataFeedKind.AltBar),
            AssetDir: AssetDir(asset),
            OutcomeFeedId: "EqD_1m_2M",
            TypeCode: "EqD",
            ThresholdAbsolute: 2_000_000m,
            ThresholdScaled: 2_000_000L,
            ThresholdUnit: "quote_asset",
            ThresholdInputMode: "absolute",
            ThresholdConvenienceInput: null,
            SourceScale: scale,
            AccumulatorScale: scale,
            MaxPartitionSizeMB: 1,
            ToolVersion: "test-1.0");

        var result = NewPipeline().Run(job, ct: TestContext.Current.CancellationToken);

        Assert.Equal(3, result.BarCount);   // 6 source bars / 2 per emit = 3
    }
}
