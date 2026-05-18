using System.Globalization;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Aggregation;
using AlgoTradeForge.HistoryLoader.Infrastructure.Storage;
using AlgoTradeForge.Infrastructure.IO;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Aggregation;

/// <summary>
/// End-to-end resume / continuation tests. The contract: a fresh build over [t0..t2N] must
/// produce a feeds.json manifest + on-disk CSV that's structurally indistinguishable from
/// (a) a fresh build over [t0..tN] followed by (b) a continue run over [tN+1..t2N] using
/// the recorded LastSourceTs / LastBrickClose anchors. "Structurally indistinguishable"
/// here means: same bar count, same partition list, same trailing-bar OHLC, and a
/// fidelity summary that's a defensible weighted merge of the two runs.
/// </summary>
public sealed class AggregationPipelineResumeTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), $"AggregationPipelineResumeTests_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string AssetDir(string asset) => Path.Combine(_tempDir, "binance", asset);
    private string CandlesDir(string asset) => Path.Combine(AssetDir(asset), "candles");

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

    private AggregationJob Job(
        string asset,
        string typeCode,
        long thresholdScaled,
        decimal thresholdAbs,
        ResumeContext? resume = null)
    {
        var scale = new ScaleContext(tickSize: 0.01m, quantityStepSize: 0.0001m);
        return new AggregationJob(
            JobId: $"job-{Guid.NewGuid():N}",
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
            ToolVersion: "test-1.0",
            Resume: resume);
    }

    private static AltBarFeedSpec ToSpec(FeedDefinition def) =>
        new(
            Kind: def.Kind ?? "OHLCV_AltBar",
            Columns: def.Columns,
            Type: def.Type!,
            Source: def.Source!,
            Threshold: def.Threshold!,
            Build: def.Build!,
            Fidelity: def.Fidelity!,
            FirstBarTs: def.FirstBarTs,
            LastBarTs: def.LastBarTs,
            Sidecar: def.Sidecar);

    /// <summary>
    /// Computes the resume cutoff the endpoint would compute: the trailing bar's tsOpen
    /// minus one (so records with <c>ts &gt;= trailingBarTsOpen</c> get reconsumed and the
    /// trailing bar is re-emitted identically). Falls back to the prior LastSourceTs for
    /// zero-bar prior runs.
    /// </summary>
    private static long ComputeResumeCutoff(FeedDefinition existing)
    {
        if (existing.LastBarTs is not null
            && long.TryParse(existing.LastBarTs, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ts))
            return ts - 1;
        return long.Parse(existing.Source!.LastTs!, CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task Run_EqVResume_ProducesSameManifestTotalsAsFreshBuild()
    {
        // Reference (fresh): build all 12 records in one run.
        const string assetRef = "BTCUSDT_REF";
        WriteCandlesEqVPattern(assetRef);

        var pipelineRef = new AggregationPipeline(
            new PartitionedSourceReader(),
            new FeedSchemaManager(new LocalFileStorage()),
            new OverwritePathWriter(),
            TimeProvider.System);
        var freshResult = await pipelineRef.Run(
            Job(assetRef, "EqV", thresholdScaled: 1000, thresholdAbs: 1000m),
            ct: TestContext.Current.CancellationToken);
        Assert.Equal(4, freshResult.BarCount);

        // Subject (resume): build first 6 records, then continue with the remaining 6.
        const string assetSub = "BTCUSDT_SUB";
        WriteCandlesEqVPattern(assetSub, firstSix: true);

        var pipelineSub = new AggregationPipeline(
            new PartitionedSourceReader(),
            new FeedSchemaManager(new LocalFileStorage()),
            new OverwritePathWriter(),
            TimeProvider.System);
        var firstHalf = await pipelineSub.Run(
            Job(assetSub, "EqV", thresholdScaled: 1000, thresholdAbs: 1000m),
            ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, firstHalf.BarCount);

        // Append remaining 6 records to the source. Mirrors a real-world ingestor extending
        // the source feed between continue calls.
        AppendCandlesEqVPattern(assetSub, secondSix: true);

        var manifest = await new FeedSchemaManager(new LocalFileStorage()).Load(AssetDir(assetSub), TestContext.Current.CancellationToken);
        var existing = manifest!.Feeds["EqV_1m_1000"];
        var resume = new ResumeContext(
            LastSourceTsMs: ComputeResumeCutoff(existing),
            LastBrickClose: existing.Build?.LastBrickClose,
            PriorSpec: ToSpec(existing));

        var resumedResult = await pipelineSub.Run(
            Job(assetSub, "EqV", thresholdScaled: 1000, thresholdAbs: 1000m, resume: resume),
            ct: TestContext.Current.CancellationToken);

        // Resume re-emits the trailing bar (1 of the prior 2) PLUS 2 new bars. Per-run total
        // = 3. The manifest merges to priorBars + newBars - 1 = 2 + 3 - 1 = 4.
        Assert.Equal(3, resumedResult.BarCount);

        var resumedManifest = (await new FeedSchemaManager(new LocalFileStorage()).Load(AssetDir(assetSub), TestContext.Current.CancellationToken))!;
        var resumedEntry = resumedManifest.Feeds["EqV_1m_1000"];
        Assert.Equal(4L, resumedEntry.Build!.BarCount);
        Assert.Equal(2, resumedEntry.Build.RunCount);
        Assert.Equal(12L, resumedEntry.Source!.RecordCount);

        // CSV partition contents must match fresh build: same row count and same final row.
        var refDir = Path.Combine(AssetDir(assetRef), "aggregated", "EqV_1m_1000");
        var subDir = Path.Combine(AssetDir(assetSub), "aggregated", "EqV_1m_1000");
        var refRows = AllDataRows(refDir);
        var subRows = AllDataRows(subDir);
        Assert.Equal(refRows.Count, subRows.Count);
        Assert.Equal(refRows[^1], subRows[^1]);
    }

    [Fact]
    public async Task Run_ResumeWithNoNewRecords_ReEmitsTrailingBarOnly()
    {
        // Pipeline-level test of the "trailing bar refresh" semantics. The endpoint would
        // short-circuit this case to no_new_data; reaching the pipeline with no new records
        // happens only via direct test injection. The pipeline still executes deterministically:
        // re-feed the trailing bar's records → re-emit the same bar. Bar count stays the
        // same; RunCount increments; LastSourceTs stays the same.
        const string asset = "ETHUSDT_NOOP";
        WriteCandlesEqVPattern(asset, firstSix: true);

        var pipeline = new AggregationPipeline(
            new PartitionedSourceReader(),
            new FeedSchemaManager(new LocalFileStorage()),
            new OverwritePathWriter(),
            TimeProvider.System);
        await pipeline.Run(
            Job(asset, "EqV", thresholdScaled: 1000, thresholdAbs: 1000m),
            ct: TestContext.Current.CancellationToken);

        var manifest = (await new FeedSchemaManager(new LocalFileStorage()).Load(AssetDir(asset), TestContext.Current.CancellationToken))!;
        var existing = manifest.Feeds["EqV_1m_1000"];
        var priorBarCount = existing.Build!.BarCount!.Value;

        var resume = new ResumeContext(
            LastSourceTsMs: ComputeResumeCutoff(existing),
            LastBrickClose: existing.Build.LastBrickClose,
            PriorSpec: ToSpec(existing));
        var resumedResult = await pipeline.Run(
            Job(asset, "EqV", thresholdScaled: 1000, thresholdAbs: 1000m, resume: resume),
            ct: TestContext.Current.CancellationToken);

        // Re-emitted trailing bar only.
        Assert.Equal(1, resumedResult.BarCount);

        var resumedManifest = (await new FeedSchemaManager(new LocalFileStorage()).Load(AssetDir(asset), TestContext.Current.CancellationToken))!;
        var resumed = resumedManifest.Feeds["EqV_1m_1000"];
        // Total bars = priorBars (2) + newBars (1) - 1 = priorBars unchanged.
        Assert.Equal(priorBarCount, resumed.Build!.BarCount);
        Assert.Equal(2, resumed.Build.RunCount);
        // Source.LastTs is updated to the last consumed record (which is the same trailing
        // record that was consumed in the prior run, since we didn't add new source data).
        Assert.Equal(existing.Source!.LastTs, resumed.Source!.LastTs);
    }

    private void WriteCandlesEqVPattern(string asset, bool firstSix = false)
    {
        // 12 records, vol=400 each → at threshold 1000, every 3 records emit a bar
        // (realized=1200, overshoot=20%). 4 bars total over 12 records.
        var rows = new List<(long ts, long o, long h, long l, long c, long v)>
        {
            (Ts(2024, 1, 1, 0),  100, 110, 95,  105, 400),
            (Ts(2024, 1, 1, 1),  105, 115, 100, 110, 400),
            (Ts(2024, 1, 1, 2),  110, 120, 105, 118, 400),
            (Ts(2024, 1, 1, 3),  118, 125, 115, 122, 400),
            (Ts(2024, 1, 1, 4),  122, 130, 120, 128, 400),
            (Ts(2024, 1, 1, 5),  128, 135, 125, 132, 400),
            (Ts(2024, 1, 1, 6),  132, 140, 130, 137, 400),
            (Ts(2024, 1, 1, 7),  137, 145, 135, 142, 400),
            (Ts(2024, 1, 1, 8),  142, 150, 140, 147, 400),
            (Ts(2024, 1, 1, 9),  147, 155, 145, 152, 400),
            (Ts(2024, 1, 1, 10), 152, 160, 150, 157, 400),
            (Ts(2024, 1, 1, 11), 157, 165, 155, 162, 400),
        };
        WriteCandles(asset, "2024-01", "1m", firstSix ? rows.Take(6).ToArray() : rows.ToArray());
    }

    private void AppendCandlesEqVPattern(string asset, bool secondSix)
    {
        // Append the remaining 6 rows to an already-written first-half candles file.
        var path = Path.Combine(CandlesDir(asset), "2024-01_1m.csv");
        var rows = new (long ts, long o, long h, long l, long c, long v)[]
        {
            (Ts(2024, 1, 1, 6),  132, 140, 130, 137, 400),
            (Ts(2024, 1, 1, 7),  137, 145, 135, 142, 400),
            (Ts(2024, 1, 1, 8),  142, 150, 140, 147, 400),
            (Ts(2024, 1, 1, 9),  147, 155, 145, 152, 400),
            (Ts(2024, 1, 1, 10), 152, 160, 150, 157, 400),
            (Ts(2024, 1, 1, 11), 157, 165, 155, 162, 400),
        };
        if (!secondSix) return;
        using var sw = new StreamWriter(path, append: true);
        foreach (var r in rows)
            sw.WriteLine($"{r.ts},{r.o},{r.h},{r.l},{r.c},{r.v}");
    }

    private static List<string> AllDataRows(string feedDir) =>
        Directory.EnumerateFiles(feedDir, "*.csv")
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .SelectMany(f => File.ReadAllLines(f).Skip(1))
            .ToList();
}
