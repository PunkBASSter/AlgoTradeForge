using System.Globalization;
using System.Text;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Aggregation.Accumulators;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlgoTradeForge.HistoryLoader.Application.Aggregation;

/// <summary>
/// One-shot batch pipeline: reads source records chronologically, feeds an accumulator, writes
/// emitted bars to a partitioned sink, promotes the staging dir atomically, and finalizes the
/// manifest entry. A single instance handles one job.
/// </summary>
public sealed class AggregationPipeline
{
    private const string OutputHeader = "ts,o,h,l,c,vol";

    private readonly PartitionedSourceReader _reader;
    private readonly ISchemaManager _schemaManager;
    private readonly OverwritePathWriter _overwriter;
    private readonly TimeProvider _clock;
    private readonly ILogger<AggregationPipeline> _logger;
    private readonly ILogger<MonotonicTickSource> _monoLogger;

    public AggregationPipeline(
        PartitionedSourceReader reader,
        ISchemaManager schemaManager,
        OverwritePathWriter overwriter,
        TimeProvider clock,
        ILogger<AggregationPipeline>? logger = null,
        ILogger<MonotonicTickSource>? monoLogger = null)
    {
        _reader = reader;
        _schemaManager = schemaManager;
        _overwriter = overwriter;
        _clock = clock;
        _logger = logger ?? NullLogger<AggregationPipeline>.Instance;
        _monoLogger = monoLogger ?? NullLogger<MonotonicTickSource>.Instance;
    }

    public AggregationResult Run(
        AggregationJob job,
        Action<ProgressEvent>? onProgress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        // Defense-in-depth: EligibilityRules rejects non-Tick Range/Renko, but a private-API
        // caller bypassing eligibility would silently aggregate from time-bar OHLC and distort
        // actual_overshoot_pct.
        if (job.TypeCode is "Range" or "Renko" && job.Source.Kind != DataFeedKind.Tick)
        {
            throw new InvalidOperationException(
                $"{job.TypeCode} requires a Tick source (got {job.Source.Kind}). " +
                "Enforced at EligibilityRules; reaching the pipeline means a caller bypassed it.");
        }

        var startedAt = _clock.GetUtcNow();
        var startTicks = _clock.GetTimestamp();

        var feedDir = Path.Combine(job.AssetDir, "aggregated", job.OutcomeFeedId);
        Directory.CreateDirectory(feedDir);

        AppendStagingPlan? appendPlan = null;
        string stagingDir;
        if (job.Resume is { } resumeContext)
        {
            var priorLastBarTs = long.Parse(
                resumeContext.PriorSpec.LastBarTs!,
                CultureInfo.InvariantCulture);
            appendPlan = _overwriter.PrepareStagingDirForAppend(feedDir, job.JobId, priorLastBarTs);
            stagingDir = appendPlan.StagingDir;
        }
        else
        {
            stagingDir = _overwriter.PrepareStagingDir(feedDir, job.JobId);
        }

        var accumulator = AccumulatorEntry.Open(
            job.TypeCode, job.ThresholdScaled, job.SourceScale, job.AccumulatorScale, job.Source.Kind);

        // Renko is the only accumulator with path-dependent state the cutoff filter can't
        // reconstruct from records alone (_lastBrickClose).
        if (job.Resume is { LastBrickClose: { } anchor }
            && accumulator is RenkoAccumulator renko)
        {
            renko.Seed(anchor);
        }

        long bytesBudget = (long)job.MaxPartitionSizeMB * 1024 * 1024;
        using var sink = new PartitionedSinkWriter(
            stagingDir, bytesBudget, OutputHeader, appendPlan?.ResumeState);

        // Imbalance-family accumulators (EqIV, EqID, EqIT) publish a sidecar (.flow) sibling
        // dir alongside the bar dir. Both stage in parallel; both promote atomically; the
        // manifest writes both entries under one exclusive lock at finalize so readers never
        // see a half-registered imbalance feed. Schema (header, columns, fidelity tags) is
        // owned by the accumulator — pipeline is shape-agnostic.
        var sidecarSchema = accumulator.SidecarSchema;
        var hasSidecar = sidecarSchema is not null;
        var sidecarFeedId = hasSidecar ? job.OutcomeFeedId + ".flow" : null;
        var sidecarFeedDir = hasSidecar ? Path.Combine(job.AssetDir, "aggregated", sidecarFeedId!) : null;
        string? sidecarStagingDir = null;
        AppendStagingPlan? sidecarAppendPlan = null;
        if (hasSidecar)
        {
            Directory.CreateDirectory(sidecarFeedDir!);
            // Sidecar rows are 1:1 with primary bars; same cutoff applies.
            if (appendPlan is not null && job.Resume is { } resumeForSidecar)
            {
                var priorLastBarTs = long.Parse(
                    resumeForSidecar.PriorSpec.LastBarTs!,
                    CultureInfo.InvariantCulture);
                sidecarAppendPlan = _overwriter.PrepareStagingDirForAppend(
                    sidecarFeedDir!, job.JobId, priorLastBarTs);
                sidecarStagingDir = sidecarAppendPlan.StagingDir;
            }
            else
            {
                sidecarStagingDir = _overwriter.PrepareStagingDir(sidecarFeedDir!, job.JobId);
            }
        }
        // Using declaration so any exception path closes the FileStream — otherwise on Windows
        // the staging dir can't be cleaned and StartupSweepService inherits a leaked handle.
        // The explicit Dispose() below is load-bearing for partition-rename ordering before
        // Promote and is idempotent.
        using PartitionedSinkWriter? sidecarSink = hasSidecar
            ? new PartitionedSinkWriter(
                sidecarStagingDir!, bytesBudget, sidecarSchema!.Header, sidecarAppendPlan?.ResumeState)
            : null;

        // Source record volume samples drive `median_source_record_value` on finalize.
        // Time-bar path keeps the exact median (small N). Tick path uses a P²-streaming
        // estimator so 5y of perp ticks (~500M records) doesn't blow allocations.
        var isTickSource = job.Source.Kind == DataFeedKind.Tick;
        var volumeSamples = isTickSource ? null : new List<long>(capacity: 1024);
        var streamingMedian = isTickSource ? new StreamingMedianEstimator() : null;

        // Strict-monotonic ts decorator wraps tick sources; bump and regression counts are
        // read out post-iteration and surfaced in stats + manifest.
        var monoSource = isTickSource ? new MonotonicTickSource(_monoLogger) : null;

        long barsEmitted = 0;
        long? firstBarTs = null;
        long lastBarTs = 0;
        long lastSourceTs = 0;
        var rowBuffer = new StringBuilder(64);

        onProgress?.Invoke(new ProgressEvent.Started(
            job.JobId, job.OutcomeFeedId, startedAt, job.Source.FeedId));

        var lastProgressTicks = startTicks;
        long sourceRecordsConsumed = 0;
        // Trailing-bar records get reconsumed for deterministic re-emit; net out that overlap
        // when merging RecordCount. priorLastSourceTs is the prior run's last consumed ts.
        long sourceRecordsBeyondPriorCutoff = 0;
        long? priorLastSourceTs = null;
        if (job.Resume?.PriorSpec.Source?.LastTs is { } priorLastTsRaw
            && long.TryParse(priorLastTsRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var priorLastTsParsed))
        {
            priorLastSourceTs = priorLastTsParsed;
        }
        string? lastEmittedMonth = null;

        // Time-bar imbalance accumulators join candle-ext to populate the imbalance fields on
        // SourceRecord (which column the joiner reads is determined by sidecarSchema.TimeBarJoinMode).
        // Spot / no-candle-ext layouts are rejected by eligibility, so reaching here with a
        // missing dir means partial coverage → drop unjoined records.
        //
        // Resume cutoff filter goes BEFORE the monotonic decorator so its bump count tracks
        // only post-cutoff records.
        IEnumerable<SourceRecord> sourceStream = _reader.Read(job.Source);
        if (job.Resume is { } resumeFilter)
            sourceStream = sourceStream.Where(r => r.TsMs > resumeFilter.LastSourceTsMs);
        if (monoSource is not null)
            sourceStream = monoSource.Read(sourceStream);
        if (hasSidecar &&
            sidecarSchema!.TimeBarJoinMode != CandleExtJoinMode.None &&
            job.Source.Kind == DataFeedKind.TimeBar)
        {
            var join = new CandleExtJoiningSource(
                job.AssetDir, job.Source.FeedId, job.SourceScale, sidecarSchema.TimeBarJoinMode);
            sourceStream = join.Join(sourceStream);
        }

        // Local helper: write one bar's CSV row + update per-bar bookkeeping. Used by both
        // the primary emit path and the Renko multi-brick drain loop.
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

        try
        {
            foreach (var record in sourceStream)
            {
                ct.ThrowIfCancellationRequested();
                sourceRecordsConsumed++;
                lastSourceTs = record.TsMs;
                if (priorLastSourceTs is null || record.TsMs > priorLastSourceTs.Value)
                    sourceRecordsBeyondPriorCutoff++;
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

                    // Renko: a single TryAdvance can stage multiple bricks; drain the queue.
                    // Drained bars carry no sidecar (Range/Renko have none, and EqIV emits
                    // exactly one bar per TryAdvance so its drain queue stays empty).
                    while (accumulator.TryDrainQueued(out var queued))
                    {
                        WriteBar(in queued);
                    }
                }

                // Throttle progress events to ~once per second to avoid drowning the SSE channel
                // on fast-source jobs.
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
        }
        catch (OperationCanceledException)
        {
            // Dispose explicitly first so the directory delete doesn't race a still-open handle
            // on Windows. PartitionedSinkWriter.Dispose() is idempotent.
            try { sink.Dispose(); } catch (Exception ex) { _logger.LogWarning(ex, "Sink dispose during cancel cleanup failed (ignored): jobId={JobId}", job.JobId); }
            try { sidecarSink?.Dispose(); } catch (Exception ex) { _logger.LogWarning(ex, "Sidecar sink dispose during cancel cleanup failed (ignored): jobId={JobId}", job.JobId); }

            DeleteStagingDirSafely(stagingDir, job.JobId);
            if (sidecarStagingDir is not null)
                DeleteStagingDirSafely(sidecarStagingDir, job.JobId);

            // Rethrow so the worker host's catch handler routes to the correct terminal state
            // (OnCancelled vs OnErrored("host_shutdown") based on which token fired).
            throw;
        }

        var stats = accumulator.Complete();
        // Source-side bump + regression counts (0 for time-bar) get folded into stats here.
        // The decorator owns both because they're properties of the source stream, not the
        // accumulator math.
        if (monoSource is not null)
            stats = stats with
            {
                MonotonicBumps = monoSource.BumpCount,
                MonotonicRegressions = monoSource.RegressionCount,
            };

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

        var medianSourceRecordValue = streamingMedian is not null
            ? streamingMedian.Median
            : ComputeMedian(volumeSamples!);
        var nFactor = medianSourceRecordValue > 0d
            ? (double)job.ThresholdScaled / medianSourceRecordValue
            : 0d;
        var estimatedOvershootPct = nFactor > 0d ? 100d / (2d * nFactor) : 0d;

        var elapsedSeconds = (_clock.GetTimestamp() - startTicks) / (double)_clock.TimestampFrequency;

        // Resume: trailing bar is truncated then re-emitted, so totalBars = priorBars + newBars - 1.
        var priorSpec = job.Resume?.PriorSpec;
        var priorBars = (double)(priorSpec?.Build?.BarCount ?? 0);
        var newBars = (double)stats.BarsEmitted;
        var totalBars = priorSpec is null
            ? (long)stats.BarsEmitted
            : (long)Math.Max(priorBars + newBars - 1d, 0d);
        var totalRecordCount = priorSpec is null
            ? sourceRecordsConsumed
            : (priorSpec.Source?.RecordCount ?? 0) + sourceRecordsBeyondPriorCutoff;
        var firstBarTsForSpec = priorSpec?.FirstBarTs ?? firstBarTs?.ToString(CultureInfo.InvariantCulture);
        var lastBarTsForSpec = barsEmitted > 0
            ? lastBarTs.ToString(CultureInfo.InvariantCulture)
            : priorSpec?.LastBarTs;
        var partitionsForSpec = priorSpec is null
            ? partitions
            : MergePartitionLists(priorSpec.Build?.PartitionsWritten ?? [], partitions);
        var monotonicBumps = priorSpec is null
            ? (isTickSource ? (long?)stats.MonotonicBumps : null)
            : (priorSpec.Build?.MonotonicBumps ?? 0L) + (isTickSource ? stats.MonotonicBumps : 0L);
        var monotonicRegressions = priorSpec is null
            ? (isTickSource ? (long?)stats.MonotonicRegressions : null)
            : (priorSpec.Build?.MonotonicRegressions ?? 0L) + (isTickSource ? stats.MonotonicRegressions : 0L);
        var runCount = (priorSpec?.Build?.RunCount ?? 1) + (priorSpec is null ? 0 : 1);

        // Bar-count-weighted merge across runs. Max overshoot is monotonic, not weighted.
        double WMerge(double? oldValue, double newValue)
        {
            if (priorBars + newBars <= 0d) return newValue;
            return ((oldValue ?? 0d) * priorBars + newValue * newBars) / (priorBars + newBars);
        }

        var mergedActualOvershoot = priorSpec is null
            ? stats.MeanOvershootPct
            : WMerge(priorSpec.Fidelity?.ActualOvershootPct, stats.MeanOvershootPct);
        var mergedMaxOvershoot = priorSpec is null
            ? stats.MaxOvershootPct
            : Math.Max(priorSpec.Fidelity?.MaxOvershootPct ?? 0d, stats.MaxOvershootPct);
        var mergedNFactor = priorSpec is null
            ? nFactor
            : WMerge(priorSpec.Fidelity?.NFactor, nFactor);
        var mergedMedian = priorSpec is null
            ? medianSourceRecordValue
            : WMerge(priorSpec.Fidelity?.MedianSourceRecordValue, medianSourceRecordValue);
        var mergedEstimatedOvershoot = priorSpec is null
            ? estimatedOvershootPct
            : WMerge(priorSpec.Fidelity?.EstimatedOvershootPct, estimatedOvershootPct);

        long? lastBrickClose = null;
        if (accumulator is RenkoAccumulator renkoAcc)
            lastBrickClose = renkoAcc.LastBrickClose;

        // Defensive fallback for zero-record runs (endpoint guards against this).
        var lastSourceTsForSpec = sourceRecordsConsumed > 0
            ? lastSourceTs.ToString(CultureInfo.InvariantCulture)
            : priorSpec?.Source?.LastTs;

        var spec = new AltBarFeedSpec(
            Kind: "OHLCV_AltBar",
            Columns: ["ts", "o", "h", "l", "c", "vol"],
            Type: new AggregatedTypeInfo { Code = job.TypeCode, Name = TypeCodeToName(job.TypeCode) },
            Source: new AggregatedSourceInfo
            {
                Feed = job.Source.FeedId,
                FirstTs = priorSpec?.Source?.FirstTs,
                LastTs = lastSourceTsForSpec,
                RecordCount = totalRecordCount,
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
                BarCount = totalBars,
                PartitionsWritten = partitionsForSpec,
                MaxPartitionSizeMB = job.MaxPartitionSizeMB,
                MonotonicBumps = monotonicBumps,
                MonotonicRegressions = monotonicRegressions,
                LastBrickClose = lastBrickClose,
                RunCount = runCount,
            },
            Fidelity: new FidelityInfo
            {
                EstimatedOvershootPct = mergedEstimatedOvershoot,
                ActualOvershootPct = mergedActualOvershoot,
                MaxOvershootPct = mergedMaxOvershoot,
                MedianSourceRecordValue = mergedMedian,
                NFactor = mergedNFactor,
                // Imbalance accumulators set the reconstruction method per their schema +
                // source kind; every other type keeps it null. The field must be present
                // (the manifest validator pins this).
                ImbalanceReconstructionMethod = hasSidecar
                    ? (isTickSource
                        ? sidecarSchema!.FidelityMethodTagTickSource
                        : sidecarSchema!.FidelityMethodTagTimeBarSource)
                    : null,
            },
            FirstBarTs: firstBarTsForSpec,
            LastBarTs: lastBarTsForSpec,
            Sidecar: sidecarFeedId);    // overridden by EnsureAltBarWithSidecar; null for non-imbalance feeds

        if (hasSidecar)
        {
            _schemaManager.EnsureAltBarWithSidecar(
                job.AssetDir, job.OutcomeFeedId, spec, sidecarFeedId!, [.. sidecarSchema!.Columns]);
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

    private void DeleteStagingDirSafely(string stagingDir, string jobId)
    {
        try
        {
            if (Directory.Exists(stagingDir))
                Directory.Delete(stagingDir, recursive: true);
        }
        catch (Exception ex)
        {
            // StartupSweepService picks up any orphan .staging-* dirs on next boot, so a failed
            // best-effort cleanup here is non-fatal — log loud and move on.
            _logger.LogWarning(ex,
                "Cancel cleanup failed to delete staging dir '{StagingDir}' (jobId={JobId}); StartupSweep will reclaim it on next boot.",
                stagingDir, jobId);
        }
    }

    // Defensive union (current run's list already includes pre-cutoff partitions copied
    // during staging). Both arrays come from canonical writer output → ordinal compare.
    private static string[] MergePartitionLists(IReadOnlyList<string> prior, IReadOnlyList<string> current)
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var p in prior) set.Add(p);
        foreach (var c in current) set.Add(c);
        return [.. set];
    }

    private static double ComputeMedian(List<long> samples)
    {
        if (samples.Count == 0) return 0d;
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
        "EqIV" => "EqualImbalance",
        "EqID" => "EqualDollarImbalance",
        "EqIT" => "EqualTickImbalance",
        "Range" => "Range",
        "Renko" => "Renko",
        _ => typeCode,
    };
}
