using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Application.Aggregation;
using AlgoTradeForge.HistoryLoader.Infrastructure.Storage;
using AlgoTradeForge.Storage;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Aggregation;

/// <summary>
/// H2: end-to-end tick-source pipeline. Phase 2a wired up <see cref="MonotonicTickSource"/>,
/// <see cref="StreamingMedianEstimator"/>, and the tick branch of
/// <see cref="PartitionedSourceReader"/>, but the unit tests for each component don't prove
/// the wiring inside <see cref="AggregationPipeline.Run"/> propagates the bump count to the
/// manifest, swaps in the streaming median, and produces the expected bar count.
/// </summary>
public sealed class AggregationPipeline_TickSourceTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), $"AggregationPipeline_TickSourceTests_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string AssetDir(string asset) =>
        Path.Combine(_tempDir, "binance", asset);

    private string TicksDir(string asset) =>
        Path.Combine(AssetDir(asset), "ticks");

    private void WriteTicks(string asset, string day, params (long ts, long price, long qty, int isBuyerMaker, long aggId)[] rows)
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

    private AggregationJob TickJob(string asset, string typeCode, long thresholdScaled, decimal thresholdAbs)
    {
        var scale = new ScaleContext(tickSize: 0.01m, quantityStepSize: 0.0001m);
        return new AggregationJob(
            JobId: "tick-job-test",
            Source: new DataFeedDescriptor(_tempDir, "binance", asset, "ticks", DataFeedKind.Tick),
            AssetDir: AssetDir(asset),
            OutcomeFeedId: $"{typeCode}_ticks_{thresholdAbs}",
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

    private static AggregationPipeline NewPipeline() => new(
        new PartitionedSourceReader(new LocalFileStorage()),
        new FeedSchemaManager(new LocalFileStorage()),
        new OverwritePathWriter(new LocalFileStorage()),
        new LocalFileStorage(),
        TimeProvider.System);

    // -------------------------------------------------------------------------
    // 1. MonotonicBumps propagates from decorator → stats → BuildInfo → manifest
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Run_TickSourceEqV_PropagatesMonotonicBumpsToManifest()
    {
        const string asset = "BTCUSDT_perp";
        // 50 ticks all sharing one ms, then 50 spaced 10 ms apart. The decorator must bump
        // 49 of the first cluster (49 = 50 - 1) onto consecutive ms values.
        long sharedTs = Ts(2024, 4, 15, 12, 0, 0);
        long spacedStart = sharedTs + 100;

        var rows = new (long ts, long price, long qty, int isBuyerMaker, long aggId)[100];
        for (int i = 0; i < 50; i++)
            rows[i] = (sharedTs, 5_000_000 + i, 10, i % 2, 1000 + i);
        for (int i = 0; i < 50; i++)
            rows[50 + i] = (spacedStart + i * 10, 5_000_500 + i, 10, i % 2, 1100 + i);

        WriteTicks(asset, "2024-04-15", rows);

        var pipeline = NewPipeline();
        var result = await pipeline.Run(
            TickJob(asset, "EqV", thresholdScaled: 200, thresholdAbs: 200m),
            ct: TestContext.Current.CancellationToken);

        // 100 ticks × qty=10 = 1000 base-vol total; threshold=200 → 5 bars.
        Assert.Equal(5, result.BarCount);

        // Manifest carries MonotonicBumps from the decorator.
        var manifest = await new FeedSchemaManager(new LocalFileStorage()).Load(AssetDir(asset), TestContext.Current.CancellationToken);
        Assert.NotNull(manifest);
        var entry = Assert.Contains($"EqV_ticks_200", manifest!.Feeds);
        Assert.NotNull(entry.Build);
        Assert.Equal(49L, entry.Build!.MonotonicBumps);
    }

    // -------------------------------------------------------------------------
    // 2. Streaming median powers NFactor on tick paths
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Run_TickSourceEqT_UsesStreamingMedian_NFactorMatchesExpected()
    {
        const string asset = "ETHUSDT_perp";
        // 100 ticks each with qty=1 — for EqT the accumulator counts records, so threshold=10
        // emits 10 bars. The pipeline's median-volume estimator runs on `record.Volume` (qty),
        // which is constant=1; n_factor = threshold(10) / median(1) = 10.
        var rows = new (long ts, long price, long qty, int isBuyerMaker, long aggId)[100];
        long start = Ts(2024, 4, 15, 12);
        for (int i = 0; i < 100; i++)
            rows[i] = (start + i * 100, 5_000_000 + i, 1, i % 2, 2000 + i);

        WriteTicks(asset, "2024-04-15", rows);

        var pipeline = NewPipeline();
        var result = await pipeline.Run(
            TickJob(asset, "EqT", thresholdScaled: 10, thresholdAbs: 10m),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(10, result.BarCount);
        // Streaming median on a constant 1-stream returns 1 exactly (n=100 → P² active path).
        Assert.Equal(1d, result.MedianSourceRecordValue, precision: 5);
        Assert.Equal(10d, result.NFactor, precision: 5);
    }

    // -------------------------------------------------------------------------
    // 3. Tick path tolerates already-monotonic input (zero bumps)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Run_TickSourceWithStrictMonotonicTimestamps_BumpsZero()
    {
        const string asset = "SOLUSDT_perp";
        var rows = new (long ts, long price, long qty, int isBuyerMaker, long aggId)[20];
        long start = Ts(2024, 4, 15, 12);
        for (int i = 0; i < 20; i++)
            rows[i] = (start + i * 100, 200_000 + i, 5, i % 2, 3000 + i);

        WriteTicks(asset, "2024-04-15", rows);

        var pipeline = NewPipeline();
        var result = await pipeline.Run(
            TickJob(asset, "EqV", thresholdScaled: 50, thresholdAbs: 50m),
            ct: TestContext.Current.CancellationToken);

        // 20 × qty=5 = 100 base-vol; threshold=50 → 2 bars.
        Assert.Equal(2, result.BarCount);

        var manifest = await new FeedSchemaManager(new LocalFileStorage()).Load(AssetDir(asset), TestContext.Current.CancellationToken);
        Assert.NotNull(manifest);
        var entry = Assert.Contains("EqV_ticks_50", manifest!.Feeds);
        Assert.Equal(0L, entry.Build!.MonotonicBumps);
    }
}
