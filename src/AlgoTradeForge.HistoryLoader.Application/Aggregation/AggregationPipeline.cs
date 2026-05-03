using System.Globalization;
using System.Text;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlgoTradeForge.HistoryLoader.Application.Aggregation;

/// <summary>
/// One-shot batch pipeline (TRD §6.2): reads source records chronologically, feeds an
/// accumulator, writes emitted bars to a partitioned sink, promotes the staging dir
/// atomically, and finalizes the manifest entry. A single instance handles one job.
/// </summary>
/// <remarks>
/// Memory model (P1b-12): peak working set is bounded by the partition writer's internal
/// buffer plus a <see cref="List{T}"/> of source-record volumes (used to compute the
/// post-hoc median for the manifest <c>fidelity</c> block). For Phase 1b time-bar sources
/// (max ~2.6M entries over 5y of 1m), the median list is &lt;25 MB. Phase 2a will switch
/// to a streaming median estimator before tick sources arrive.
///
/// Progress reporting goes through the optional <c>onProgress</c> callback. Phase 1b's worker
/// host wraps this with a <c>ChannelWriter&lt;ProgressEvent&gt;</c> for SSE; tests can
/// observe events via a list-appender.
/// </remarks>
public sealed class AggregationPipeline
{
    private const string OutputHeader = "ts,o,h,l,c,vol";
    private const string SidecarHeader = "ts,signed_imbalance,buy_volume,sell_volume,realized_threshold";

    private static readonly string[] SidecarColumns =
        ["signed_imbalance", "buy_volume", "sell_volume", "realized_threshold"];

    private readonly PartitionedSourceReader _reader;
    private readonly ISchemaManager _schemaManager;
    private readonly OverwritePathWriter _overwriter;
    private readonly TimeProvider _clock;
    private readonly ILogger<AggregationPipeline> _logger;

    public AggregationPipeline(
        PartitionedSourceReader reader,
        ISchemaManager schemaManager,
        OverwritePathWriter overwriter,
        TimeProvider clock,
        ILogger<AggregationPipeline>? logger = null)
    {
        _reader = reader;
        _schemaManager = schemaManager;
        _overwriter = overwriter;
        _clock = clock;
        _logger = logger ?? NullLogger<AggregationPipeline>.Instance;
    }

    public AggregationResult Run(
        AggregationJob job,
        Action<ProgressEvent>? onProgress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        // Phase 5 (ADR D1) defense-in-depth: EligibilityRules already rejects non-Tick
        // Range/Renko, but a private-API caller bypassing eligibility would otherwise
        // silently aggregate from time-bar OHLC and distort actual_overshoot_pct (the
        // failure mode the tick-only restriction exists to prevent).
        if (job.TypeCode is "Range" or "Renko" && job.Source.Kind != DataFeedKind.Tick)
        {
            throw new InvalidOperationException(
                $"{job.TypeCode} requires a Tick source (got {job.Source.Kind}). " +
                "Enforced at EligibilityRules; reaching the pipeline means a caller bypassed it.");
        }

        var startedAt = _clock.GetUtcNow();
        var startTicks = _clock.GetTimestamp();

        var feedDir = Path.Combine(job.AssetDir, "aggregated", job.OutcomeFeedId);
        // Stage path is created by the writer; promote happens atomically once the run completes.
        Directory.CreateDirectory(feedDir);
        var stagingDir = _overwriter.PrepareStagingDir(feedDir, job.JobId);

        var accumulator = AccumulatorEntry.Open(
            job.TypeCode, job.ThresholdScaled, job.SourceScale, job.AccumulatorScale);

        long bytesBudget = (long)job.MaxPartitionSizeMB * 1024 * 1024;
        using var sink = new PartitionedSinkWriter(stagingDir, bytesBudget, OutputHeader);

        // Phase 2b: EqI publishes a sidecar (.flow) sibling dir alongside the bar dir.
        // Both stage in parallel; both promote atomically; the manifest writes both entries
        // under one exclusive lock at finalize so readers never see a half-registered EqI feed.
        var isEqI = string.Equals(job.TypeCode, "EqI", StringComparison.Ordinal);
        var sidecarFeedId = isEqI ? job.OutcomeFeedId + ".flow" : null;
        var sidecarFeedDir = isEqI ? Path.Combine(job.AssetDir, "aggregated", sidecarFeedId!) : null;
        string? sidecarStagingDir = null;
        PartitionedSinkWriter? sidecarSink = null;
        if (isEqI)
        {
            Directory.CreateDirectory(sidecarFeedDir!);
            sidecarStagingDir = _overwriter.PrepareStagingDir(sidecarFeedDir!, job.JobId);
            sidecarSink = new PartitionedSinkWriter(sidecarStagingDir, bytesBudget, SidecarHeader);
        }

        // Source record volume samples — drives `median_source_record_value` on finalize.
        // Time-bar path keeps the exact median (small N, ~25 MB worst case for 5y of 1m).
        // Tick path swaps in a P²-streaming estimator so 5y of perp ticks (~500M records)
        // doesn't blow allocations (Phase 2a, see <see cref="StreamingMedianEstimator"/>).
        var isTickSource = job.Source.Kind == DataFeedKind.Tick;
        var volumeSamples = isTickSource ? null : new List<long>(capacity: 1024);
        var streamingMedian = isTickSource ? new StreamingMedianEstimator() : null;

        // Strict-monotonic ts decorator (TRD §6.3 / P2a-6) wraps the reader for tick sources;
        // bump count is read out post-iteration and surfaced in stats + manifest.
        var monoSource = isTickSource ? new MonotonicTickSource() : null;

        long barsEmitted = 0;
        long? firstBarTs = null;
        long lastBarTs = 0;
        var rowBuffer = new StringBuilder(64);

        onProgress?.Invoke(new ProgressEvent.Started(
            job.JobId, job.OutcomeFeedId, startedAt, job.Source.FeedId));

        var lastProgressTicks = startTicks;
        long sourceRecordsConsumed = 0;
        string? lastEmittedMonth = null;

        // Phase 2b: time-bar EqI joins candle-ext for its m1_taker_buy_proxy reconstruction
        // (TRD §6.2). Spot/no-candle-ext layouts are rejected at eligibility (§7), so reaching
        // here with a missing dir means partial coverage → drop unjoined records (TRD §6.2).
        IEnumerable<SourceRecord> sourceStream = _reader.Read(job.Source);
        if (monoSource is not null)
            sourceStream = monoSource.Read(sourceStream);
        if (isEqI && job.Source.Kind == DataFeedKind.TimeBar)
        {
            var join = new CandleExtJoiningSource(job.AssetDir, job.Source.FeedId, job.SourceScale);
            sourceStream = join.Join(sourceStream);
        }

        // Local helper: write one bar's CSV row + update per-bar bookkeeping. Used by both
        // the primary emit path and the Phase-5 multi-brick drain loop. Captures rowBuffer,
        // sink, firstBarTs, lastBarTs, barsEmitted, lastEmittedMonth from the enclosing scope.
        void WriteBar(in AggregatedBar bar)
        {
            rowBuffer.Clear();
            rowBuffer.Append(bar.TsMs.ToString(CultureInfo.InvariantCulture)).Append(',')
                     .Append(bar.Open.ToString(CultureInfo.InvariantCulture)).Append(',')
                     .Append(bar.High.ToString(CultureInfo.InvariantCulture)).Append(',')
                     .Append(bar.Low.ToString(CultureInfo.InvariantCulture)).Append(',')
                     .Append(bar.Close.ToString(CultureInfo.InvariantCulture)).Append(',')
                     .Append(bar.Volume.ToString(CultureInfo.InvariantCulture));
            sink.WriteRow(bar.TsMs, rowBuffer.ToString());
            firstBarTs ??= bar.TsMs;
            lastBarTs = bar.TsMs;
            barsEmitted++;
            lastEmittedMonth = MonthKey(bar.TsMs);
        }

        foreach (var record in sourceStream)
        {
            ct.ThrowIfCancellationRequested();
            sourceRecordsConsumed++;
            if (streamingMedian is not null)
                streamingMedian.Add(record.Volume);
            else
                volumeSamples!.Add(record.Volume);

            if (accumulator.TryAdvance(in record, out var bar))
            {
                WriteBar(in bar);

                if (sidecarSink is not null && accumulator.TryGetLastSidecarRow(out var sidecar))
                {
                    rowBuffer.Clear();
                    rowBuffer.Append(sidecar.TsMs.ToString(CultureInfo.InvariantCulture)).Append(',')
                             .Append(sidecar.SignedImbalance.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                             .Append(sidecar.BuyVolume.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                             .Append(sidecar.SellVolume.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                             .Append(sidecar.RealizedThreshold.ToString("R", CultureInfo.InvariantCulture));
                    sidecarSink.WriteRow(sidecar.TsMs, rowBuffer.ToString());
                }

                // Phase 5 (Renko): a single TryAdvance can stage multiple bricks. Drain the
                // queue here. Drained bars carry no sidecar (ADR D7) — Range/Renko have no
                // sidecar in v1, and EqI emits exactly one bar per TryAdvance so its drain
                // queue stays empty.
                while (accumulator.TryDrainQueued(out var queued))
                {
                    WriteBar(in queued);
                }
            }

            // Throttle progress events to ~once per second to avoid drowning the SSE channel
            // on fast-source jobs. The clock seam (P1b-15) makes this testable.
            var nowTicks = _clock.GetTimestamp();
            if (onProgress is not null &&
                (nowTicks - lastProgressTicks) > _clock.TimestampFrequency)
            {
                onProgress(new ProgressEvent.Progress(
                    job.JobId,
                    CurrentPartition: lastEmittedMonth,
                    BarsEmitted: barsEmitted,
                    ElapsedMs: (long)((nowTicks - startTicks) / (double)_clock.TimestampFrequency * 1000)));
                lastProgressTicks = nowTicks;
            }
        }

        var stats = accumulator.Finalize();
        // Phase 2a: source-side bump count (always 0 for time-bar) gets folded into stats here.
        // The decorator owns the count because it's a property of the source stream, not the
        // accumulator math.
        if (monoSource is not null)
            stats = stats with { MonotonicBumps = monoSource.BumpCount };

        // Force any in-progress partition file to atomic-rename to its final name before promote.
        sink.Dispose();
        sidecarSink?.Dispose();

        // Enumerate produced partitions BEFORE the staging→live rename so we report the
        // canonical filenames (without the path prefix) regardless of the post-promote layout.
        var partitions = Directory
            .EnumerateFiles(stagingDir, "*.csv", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .OfType<string>()
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

        _overwriter.Promote(feedDir, stagingDir);
        if (sidecarSink is not null)
            _overwriter.Promote(sidecarFeedDir!, sidecarStagingDir!);

        // Compute fidelity stats (TRD §6.4).
        var medianSourceRecordValue = streamingMedian is not null
            ? streamingMedian.Median
            : ComputeMedian(volumeSamples!);
        var nFactor = medianSourceRecordValue > 0d
            ? (double)job.ThresholdScaled / medianSourceRecordValue
            : 0d;
        var estimatedOvershootPct = nFactor > 0d ? 100d / (2d * nFactor) : 0d;

        var elapsedSeconds = (_clock.GetTimestamp() - startTicks) / (double)_clock.TimestampFrequency;

        // Manifest write.
        var spec = new AltBarFeedSpec(
            Kind: "OHLCV_AltBar",
            Columns: ["ts", "o", "h", "l", "c", "vol"],
            Type: new AggregatedTypeInfo { Code = job.TypeCode, Name = TypeCodeToName(job.TypeCode) },
            Source: new AggregatedSourceInfo
            {
                Feed = job.Source.FeedId,
                RecordCount = sourceRecordsConsumed,
            },
            Threshold: new ThresholdInfo
            {
                Value = job.ThresholdAbsolute,
                Unit = job.ThresholdUnit,
                InputMode = job.ThresholdInputMode,
                ConvenienceInput = job.ThresholdConvenienceInput,
            },
            Build: new BuildInfo
            {
                ToolVersion = job.ToolVersion,
                BuiltAt = _clock.GetUtcNow().ToString("o", CultureInfo.InvariantCulture),
                DurationSeconds = elapsedSeconds,
                BarCount = stats.BarsEmitted,
                PartitionsWritten = partitions,
                MaxPartitionSizeMB = job.MaxPartitionSizeMB,
                MonotonicBumps = isTickSource ? stats.MonotonicBumps : null,
            },
            Fidelity: new FidelityInfo
            {
                EstimatedOvershootPct = estimatedOvershootPct,
                ActualOvershootPct = stats.MeanOvershootPct,
                MaxOvershootPct = stats.MaxOvershootPct,
                MedianSourceRecordValue = medianSourceRecordValue,
                NFactor = nFactor,
                // EqI sets the reconstruction method per its source kind (TRD §4 / §6.3);
                // every other type keeps it null but the field MUST be present (validator pins this).
                ImbalanceReconstructionMethod = isEqI
                    ? (job.Source.Kind == DataFeedKind.Tick ? "tick_signed" : "m1_taker_buy_proxy")
                    : null,
            },
            FirstBarTs: firstBarTs?.ToString(CultureInfo.InvariantCulture),
            LastBarTs: barsEmitted > 0 ? lastBarTs.ToString(CultureInfo.InvariantCulture) : null,
            Sidecar: sidecarFeedId);    // overridden by EnsureAltBarWithSidecar for EqI; null for others

        if (isEqI)
        {
            _schemaManager.EnsureAltBarWithSidecar(
                job.AssetDir, job.OutcomeFeedId, spec, sidecarFeedId!, SidecarColumns);
        }
        else
        {
            _schemaManager.EnsureAltBarFeed(job.AssetDir, job.OutcomeFeedId, spec);
        }

        var result = new AggregationResult(
            JobId: job.JobId,
            OutcomeFeedId: job.OutcomeFeedId,
            BarCount: stats.BarsEmitted,
            PartitionsWritten: partitions,
            FirstBarTs: spec.FirstBarTs,
            LastBarTs: spec.LastBarTs,
            ActualOvershootPct: stats.MeanOvershootPct,
            MaxOvershootPct: stats.MaxOvershootPct,
            EstimatedOvershootPct: estimatedOvershootPct,
            MedianSourceRecordValue: medianSourceRecordValue,
            NFactor: nFactor,
            DurationSeconds: elapsedSeconds,
            SidecarFeedId: sidecarFeedId);

        onProgress?.Invoke(new ProgressEvent.Complete(result));

        _logger.LogInformation(
            "Aggregation complete: jobId={JobId} feedId={FeedId} bars={BarCount} duration={Seconds:F2}s",
            job.JobId, job.OutcomeFeedId, stats.BarsEmitted, elapsedSeconds);

        return result;
    }

    private static string MonthKey(long tsMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(tsMs).UtcDateTime
            .ToString("yyyy-MM", CultureInfo.InvariantCulture);

    private static double ComputeMedian(List<long> samples)
    {
        if (samples.Count == 0) return 0d;
        // In-place sort is fine — the list is owned by this run and discarded after.
        samples.Sort();
        var n = samples.Count;
        return n % 2 == 1
            ? samples[n / 2]
            : (samples[n / 2 - 1] + samples[n / 2]) / 2d;
    }

    private static string TypeCodeToName(string typeCode) => typeCode switch
    {
        "EqT" => "EqualTick",
        "EqV" => "EqualVolume",
        "EqD" => "EqualDollar",
        "EqI" => "EqualImbalance",
        "Range" => "Range",
        "Renko" => "Renko",
        _ => typeCode,
    };
}
