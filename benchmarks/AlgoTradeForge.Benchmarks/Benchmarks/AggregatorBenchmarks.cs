using System.Globalization;
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
    private string _tickAssetDir = null!;
    private AggregationPipeline _pipeline = null!;
    private AggregationJob _eqVJob = null!;
    private AggregationJob _eqTJob = null!;
    private AggregationJob _eqVTickJob = null!;
    private AggregationJob _eqTTickJob = null!;
    private AggregationJob _rangeTickJob = null!;
    private AggregationJob _renkoTickJob = null!;

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
            thresholdAbsolute: 100_000m, thresholdScaled: 100_000, scale, _assetDir);
        _eqTJob = MakeJob(source, "EqT", outcomeFeedId: "EqT_1h_500",
            thresholdAbsolute: 500m, thresholdScaled: 500, scale, _assetDir);

        // P2a-9: synthetic tick scenarios. ~150k ticks / 1h matches BTCUSDT_perp burst rate
        // without bloating the repo with real-trade CSVs (the benchmark's purpose is regression
        // detection, not real-world parity — TickSourceParityTests covers parity).
        _tickAssetDir = Path.Combine(_tempDir, "binance", "BTCUSDT_perp");
        var ticksDir = Path.Combine(_tickAssetDir, "ticks");
        Directory.CreateDirectory(ticksDir);
        WriteSyntheticTicks(Path.Combine(ticksDir, "2024-04-15.csv"), tickCount: 150_000, seed: 42);

        var tickSource = new DataFeedDescriptor(_tempDir, "binance", "BTCUSDT_perp", "ticks", DataFeedKind.Tick);
        _eqVTickJob = MakeJob(tickSource, "EqV", outcomeFeedId: "EqV_ticks_100k",
            thresholdAbsolute: 100_000m, thresholdScaled: 100_000, scale, _tickAssetDir);
        _eqTTickJob = MakeJob(tickSource, "EqT", outcomeFeedId: "EqT_ticks_500",
            thresholdAbsolute: 500m, thresholdScaled: 500, scale, _tickAssetDir);

        // P5-15: Range/Renko tick scenarios. Threshold is a price magnitude (`unit=price`);
        // synthetic walk drifts ±$5/tick, so a $50 threshold (5000 ticks under tickSize=0.01)
        // produces a substantive bar count over 150k ticks. Empirical tuning may shift these
        // if the bar-count distribution is too sparse on real BTCUSDT_perp data.
        _rangeTickJob = MakeJob(tickSource, "Range", outcomeFeedId: "Range_ticks_50",
            thresholdAbsolute: 50m, thresholdScaled: 5_000, scale, _tickAssetDir, unit: "price");
        _renkoTickJob = MakeJob(tickSource, "Renko", outcomeFeedId: "Renko_ticks_50",
            thresholdAbsolute: 50m, thresholdScaled: 5_000, scale, _tickAssetDir, unit: "price");
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
        ResetAssetOutput(_assetDir);
        ResetAssetOutput(_tickAssetDir);
    }

    private static void ResetAssetOutput(string assetDir)
    {
        var aggregatedDir = Path.Combine(assetDir, "aggregated");
        if (Directory.Exists(aggregatedDir))
            Directory.Delete(aggregatedDir, recursive: true);
        var feedsJson = Path.Combine(assetDir, "feeds.json");
        if (File.Exists(feedsJson)) File.Delete(feedsJson);
    }

    [Benchmark]
    public AggregationResult Aggregate_EqV_1h_100k() => _pipeline.Run(_eqVJob);

    [Benchmark]
    public AggregationResult Aggregate_EqT_1h_500() => _pipeline.Run(_eqTJob);

    [Benchmark]
    public AggregationResult Aggregate_EqV_FromTicks_1h() => _pipeline.Run(_eqVTickJob);

    [Benchmark]
    public AggregationResult Aggregate_EqT_FromTicks_1h() => _pipeline.Run(_eqTTickJob);

    [Benchmark]
    public AggregationResult Aggregate_Range_FromTicks_1h() => _pipeline.Run(_rangeTickJob);

    [Benchmark]
    public AggregationResult Aggregate_Renko_FromTicks_1h() => _pipeline.Run(_renkoTickJob);

    private static AggregationJob MakeJob(
        DataFeedDescriptor source, string typeCode, string outcomeFeedId,
        decimal thresholdAbsolute, long thresholdScaled, ScaleContext scale, string assetDir,
        string unit = "base_asset") =>
        new(
            JobId: $"bench-{typeCode}-{outcomeFeedId}",
            Source: source,
            AssetDir: assetDir,
            OutcomeFeedId: outcomeFeedId,
            TypeCode: typeCode,
            ThresholdAbsolute: thresholdAbsolute,
            ThresholdScaled: thresholdScaled,
            ThresholdUnit: unit,
            ThresholdInputMode: "absolute",
            ThresholdConvenienceInput: null,
            SourceScale: scale,
            AccumulatorScale: scale,
            MaxPartitionSizeMB: 100,
            ToolVersion: "bench");

    /// <summary>
    /// Generates a deterministic synthetic 1-hour tick stream. Inter-arrival times are
    /// roughly Poisson (geometric in ms) and quantities are exponential — produces realistic
    /// burstiness so the monotonicity bumper sees real work without checking a 60 MB fixture
    /// into git.
    /// </summary>
    private static void WriteSyntheticTicks(string path, int tickCount, int seed)
    {
        var rng = new Random(seed);
        long ts = new DateTimeOffset(2024, 4, 15, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        long aggId = 1_000_000;
        long price = 5_000_000; // $50,000.00 in 0.01 ticks

        using var sw = new StreamWriter(path);
        sw.WriteLine("ts,price,qty,is_buyer_maker,agg_id");

        for (int i = 0; i < tickCount; i++)
        {
            // Inter-arrival: 0..50 ms (mean ~24 ms → ~150k ticks/h matches BTCUSDT_perp burst rate).
            // Use 0 sometimes to exercise the monotonicity bumper.
            ts += rng.Next(0, 50);

            // Random walk on price, ±$5 per tick.
            price += rng.Next(-500, 501);
            if (price < 1_000_000) price = 1_000_000;

            // qty: exponential-ish (most small, some large)
            long qty = 1 + (long)(-Math.Log(1 - rng.NextDouble()) * 100);

            int isBuyerMaker = rng.Next(2);
            sw.Write(ts.ToString(CultureInfo.InvariantCulture));
            sw.Write(',');
            sw.Write(price.ToString(CultureInfo.InvariantCulture));
            sw.Write(',');
            sw.Write(qty.ToString(CultureInfo.InvariantCulture));
            sw.Write(',');
            sw.Write(isBuyerMaker.ToString(CultureInfo.InvariantCulture));
            sw.Write(',');
            sw.WriteLine((aggId++).ToString(CultureInfo.InvariantCulture));
        }
    }
}
