using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Application.Aggregation;
using AlgoTradeForge.HistoryLoader.Infrastructure.Storage;
using BenchmarkDotNet.Attributes;

namespace AlgoTradeForge.Benchmarks.Benchmarks;

/// <summary>
/// P1b-42 / P1b-43 — alt-bar aggregation pipeline throughput. Runs <see cref="AggregationPipeline"/>
/// against the bundled 5y BTCUSDT 1h slice, end-to-end (source reader → accumulator → sink
/// writer → manifest finalize). Mirrors <see cref="BacktestBenchmarks"/> conventions:
/// <c>[MemoryDiagnoser]</c> + <c>[Config(typeof(BriefJsonConfig))]</c> for the
/// <c>save-baseline.ps1</c> / <c>compare-baseline.ps1</c> ingestion.
/// </summary>
/// <remarks>
/// Each iteration writes to a per-process temp dir; <see cref="IterationCleanup"/> resets the
/// output between iterations so the partition writer always starts clean.
/// </remarks>
[MemoryDiagnoser]
[Config(typeof(BriefJsonConfig))]
public class AggregatorBenchmarks
{
    private string _tempDir = null!;
    private string _assetDir = null!;
    private AggregationPipeline _pipeline = null!;
    private AggregationJob _eqVJob = null!;
    private AggregationJob _eqTJob = null!;

    [GlobalSetup]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"AggBench_{Guid.NewGuid():N}");
        var candlesDir = Path.Combine(_tempDir, "binance", "BTCUSDT", "candles");
        _assetDir = Path.Combine(_tempDir, "binance", "BTCUSDT");
        Directory.CreateDirectory(candlesDir);

        // Copy the bundled 5y BTCUSDT 1h candles into the temp candles/ layout.
        var bundledDir = Path.Combine(AppContext.BaseDirectory, "data", "BTCUSDT_1h");
        foreach (var src in Directory.EnumerateFiles(bundledDir, "*.csv"))
        {
            var dst = Path.Combine(candlesDir, Path.GetFileName(src));
            File.Copy(src, dst, overwrite: true);
        }

        _pipeline = new AggregationPipeline(
            new PartitionedSourceReader(),
            new FeedSchemaManager(),
            new OverwritePathWriter(),
            TimeProvider.System);

        var scale = new ScaleContext(0.01m);   // 2 decimal digits → ScaleFactor=100
        var source = new DataFeedDescriptor(_tempDir, "binance", "BTCUSDT", "1h", DataFeedKind.TimeBar);

        // Thresholds chosen to land in the 50–500 emitted-bars range over the 5y / 1h source
        // (~43,800 records). Small enough to exercise the partition writer's accumulation
        // pattern without producing trivially few bars.
        _eqVJob = MakeJob(source, "EqV", outcomeFeedId: "EqV_1h_100k",
            thresholdAbsolute: 100_000m, thresholdScaled: 100_000, scale);
        _eqTJob = MakeJob(source, "EqT", outcomeFeedId: "EqT_1h_500",
            thresholdAbsolute: 500m, thresholdScaled: 500, scale);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        // Reset output between iterations so each timed call starts from a clean state.
        var aggregatedDir = Path.Combine(_assetDir, "aggregated");
        if (Directory.Exists(aggregatedDir))
            Directory.Delete(aggregatedDir, recursive: true);
        var feedsJson = Path.Combine(_assetDir, "feeds.json");
        if (File.Exists(feedsJson)) File.Delete(feedsJson);
    }

    [Benchmark]
    public AggregationResult Aggregate_EqV_1h_100k() => _pipeline.Run(_eqVJob);

    [Benchmark]
    public AggregationResult Aggregate_EqT_1h_500() => _pipeline.Run(_eqTJob);

    private AggregationJob MakeJob(
        DataFeedDescriptor source, string typeCode, string outcomeFeedId,
        decimal thresholdAbsolute, long thresholdScaled, ScaleContext scale) =>
        new(
            JobId: $"bench-{typeCode}",
            Source: source,
            AssetDir: _assetDir,
            OutcomeFeedId: outcomeFeedId,
            TypeCode: typeCode,
            ThresholdAbsolute: thresholdAbsolute,
            ThresholdScaled: thresholdScaled,
            ThresholdUnit: "base_asset",
            ThresholdInputMode: "absolute",
            ThresholdConvenienceInput: null,
            SourceScale: scale,
            AccumulatorScale: scale,
            MaxPartitionSizeMB: 100,
            ToolVersion: "bench");
}
