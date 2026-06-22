using System.Globalization;
using System.Text;
using AlgoTradeForge.Storage;
using AlgoTradeForge.Domain.Aggregation;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
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
    private readonly IFileStorage _storage;
    private readonly TimeProvider _clock;
    private readonly ILogger<AggregationPipeline> _logger;
    private readonly ILogger<MonotonicTickSource> _monoLogger;

    public AggregationPipeline(
        PartitionedSourceReader reader,
        ISchemaManager schemaManager,
        OverwritePathWriter overwriter,
        IFileStorage storage,
        TimeProvider clock,
        ILogger<AggregationPipeline>? logger = null,
        ILogger<MonotonicTickSource>? monoLogger = null)
    {
        _reader = reader;
        _schemaManager = schemaManager;
        _overwriter = overwriter;
        _storage = storage;
        _clock = clock;
        _logger = logger ?? NullLogger<AggregationPipeline>.Instance;
        _monoLogger = monoLogger ?? NullLogger<MonotonicTickSource>.Instance;
    }

    public async Task<AggregationResult> Run(
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

        AppendStagingPlan? appendPlan = null;
        string stagingDir;
        if (job.Resume is { } resumeContext)
        {
            var priorLastBarTs = long.Parse(
                resumeContext.PriorSpec.LastBarTs!,
                CultureInfo.InvariantCulture);
            appendPlan = await _overwriter.PrepareStagingDirForAppend(feedDir, job.JobId, priorLastBarTs, ct);
            stagingDir = appendPlan.StagingDir;
        }
        else
        {
            stagingDir = await _overwriter.PrepareStagingDir(feedDir, job.JobId, ct);
        }

        var accumulator = AccumulatorEntry.Open(
            job.TypeCode, job.ThresholdScaled, job.SourceScale, job.AccumulatorScale, job.Source.Kind);

        // Renko is the only accumulator with path-dependent state the cutoff filter can't
        // reconstruct from records alone (_lastBrickClose).
        if (job.Resume is { LastBrickClose: { } anchor })
        {
            accumulator.SeedResumeState(anchor);
        }

        long bytesBudget = (long)job.MaxPartitionSizeMB * 1024 * 1024;
        // await using covers exceptions other than OperationCanceledException (the explicit
        // OCE catch below disposes ahead of staging-dir cleanup so the .tmp handle is released
        // before DeleteByPrefix on Windows). DisposeAsync is idempotent — the second call from
        // scope-exit is a no-op once Complete or the OCE catch has run.
        await using var sink = await PartitionedSinkWriter.Open(
            _storage, stagingDir, bytesBudget, OutputHeader, appendPlan?.ResumeState, ct);

        // Imbalance-family accumulators (EqIV, EqID, EqIT) publish a sidecar (.flow) sibling
        // dir alongside the bar dir. Both stage in parallel; both promote per-key; the manifest
        // writes both entries under one exclusive lock at finalize so readers never see a
        // half-registered imbalance feed. Schema (header, columns, fidelity tags) is owned by
        // the accumulator — pipeline is shape-agnostic.
        var sidecarSchema = accumulator.SidecarSchema;
        var hasSidecar = sidecarSchema is not null;
        var sidecarFeedId = hasSidecar ? job.OutcomeFeedId + ".flow" : null;
        var sidecarFeedDir = hasSidecar ? Path.Combine(job.AssetDir, "aggregated", sidecarFeedId!) : null;
        string? sidecarStagingDir = null;
        AppendStagingPlan? sidecarAppendPlan = null;
        if (hasSidecar)
        {
            // Sidecar rows are 1:1 with primary bars; same cutoff applies.
            if (appendPlan is not null && job.Resume is { } resumeForSidecar)
            {
                var priorLastBarTs = long.Parse(
                    resumeForSidecar.PriorSpec.LastBarTs!,
                    CultureInfo.InvariantCulture);
                sidecarAppendPlan = await _overwriter.PrepareStagingDirForAppend(
                    sidecarFeedDir!, job.JobId, priorLastBarTs, ct);
                sidecarStagingDir = sidecarAppendPlan.StagingDir;
            }
            else
            {
                sidecarStagingDir = await _overwriter.PrepareStagingDir(sidecarFeedDir!, job.JobId, ct);
            }
        }
        await using PartitionedSinkWriter? sidecarSink = hasSidecar
            ? await PartitionedSinkWriter.Open(
                _storage, sidecarStagingDir!, bytesBudget, sidecarSchema!.Header, sidecarAppendPlan?.ResumeState, ct)
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
        IAsyncEnumerable<SourceRecord> sourceStream = _reader.Read(job.Source, ct: ct);
        if (job.Resume is { } resumeFilter)
        {
            var cutoff = resumeFilter.LastSourceTsMs;
            sourceStream = FilterAfterCutoff(sourceStream, cutoff, ct);
        }
        if (monoSource is not null)
            sourceStream = monoSource.Read(sourceStream, ct);
        if (hasSidecar &&
            sidecarSchema!.TimeBarJoinMode != CandleExtJoinMode.None &&
            job.Source.Kind == DataFeedKind.TimeBar)
        {
            var join = new CandleExtJoiningSource(
                _storage, job.AssetDir, job.Source.FeedId, job.SourceScale, sidecarSchema.TimeBarJoinMode);
            sourceStream = join.Join(sourceStream, ct);
        }

        // Local helper: write one bar's CSV row + update per-bar bookkeeping. Used by both
        // the primary emit path and the Renko multi-brick drain loop.
        async Task WriteBar(AggregatedBar bar)
        {
            rowBuffer.Clear();
            rowBuffer.Append(bar.TsMs.ToString(CultureInfo.InvariantCulture)).Append(',')
                     .Append(bar.Open.ToString(CultureInfo.InvariantCulture)).Append(',')
                     .Append(bar.High.ToString(CultureInfo.InvariantCulture)).Append(',')
                     .Append(bar.Low.ToString(CultureInfo.InvariantCulture)).Append(',')
                     .Append(bar.Close.ToString(CultureInfo.InvariantCulture)).Append(',')
                     .Append(bar.Volume.ToString(CultureInfo.InvariantCulture));
            await sink.WriteRow(bar.TsMs, rowBuffer.ToString(), ct);
            firstBarTs ??= bar.TsMs;
            lastBarTs = bar.TsMs;
            barsEmitted++;
            lastEmittedMonth = MonthKey(bar.TsMs);
        }

        try
        {
            await foreach (var record in sourceStream.WithCancellation(ct))
            {
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
                    await WriteBar(bar);

                    if (sidecarSink is not null && accumulator.TryGetLastSidecarRow(out var sidecar))
                    {
                        rowBuffer.Clear();
                        rowBuffer.Append(sidecar.TsMs.ToString(CultureInfo.InvariantCulture)).Append(',')
                                 .Append(sidecar.SignedImbalance.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                                 .Append(sidecar.BuyVolume.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                                 .Append(sidecar.SellVolume.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                                 .Append(sidecar.RealizedThreshold.ToString("R", CultureInfo.InvariantCulture));
                        await sidecarSink.WriteRow(sidecar.TsMs, rowBuffer.ToString(), ct);
                    }

                    // Renko: a single TryAdvance can stage multiple bricks; drain the queue.
                    // Drained bars carry no sidecar (Range/Renko have none, and EqIV emits
                    // exactly one bar per TryAdvance so its drain queue stays empty).
                    while (accumulator.TryDrainQueued(out var queued))
                    {
                        await WriteBar(queued);
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
            // Dispose explicitly so any open partition session aborts cleanly. DisposeAsync on
            // a writer with no Commit aborts the in-flight session (no published key) and any
            // staging keys already published get cleaned up below.
            try { await sink.DisposeAsync(); } catch (Exception ex) { _logger.LogWarning(ex, "Sink dispose during cancel cleanup failed (ignored): jobId={JobId}", job.JobId); }
            if (sidecarSink is not null)
            {
                try { await sidecarSink.DisposeAsync(); } catch (Exception ex) { _logger.LogWarning(ex, "Sidecar sink dispose during cancel cleanup failed (ignored): jobId={JobId}", job.JobId); }
            }

            await DeleteStagingDirSafely(stagingDir, job.JobId, ct);
            if (sidecarStagingDir is not null)
                await DeleteStagingDirSafely(sidecarStagingDir, job.JobId, ct);

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

        // Commit any in-flight partition session so its bytes are published as the final key
        // before promote enumerates the staging contents.
        await sink.Complete(ct);
        if (sidecarSink is not null)
            await sidecarSink.Complete(ct);

        // Enumerate produced partitions BEFORE the staging→live moves so we report the
        // canonical filenames (without the path prefix) regardless of the post-promote layout.
        var partitions = new List<string>();
        await foreach (var stagingKey in _storage.ListKeys(stagingDir, suffix: ".csv", recursive: false, ct))
        {
            var name = Path.GetFileName(stagingKey);
            if (string.IsNullOrEmpty(name)) continue;
            partitions.Add(name);
        }
        partitions.Sort(StringComparer.Ordinal);

        await _overwriter.Promote(feedDir, stagingDir, ct);
        if (sidecarSink is not null)
            await _overwriter.Promote(sidecarFeedDir!, sidecarStagingDir!, ct);

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
        string[] partitionsForSpec = priorSpec is null
            ? [.. partitions]
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
        if (accumulator.TryGetResumeState(out var resumeClose))
            lastBrickClose = resumeClose;

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
            await _schemaManager.EnsureAltBarWithSidecar(
                job.AssetDir, job.OutcomeFeedId, spec, sidecarFeedId!, [.. sidecarSchema!.Columns], ct);
        }
        else
        {
            await _schemaManager.EnsureAltBarFeed(job.AssetDir, job.OutcomeFeedId, spec, ct);
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

    private async Task DeleteStagingDirSafely(string stagingDir, string jobId, CancellationToken ct)
    {
        try
        {
            await _storage.DeleteByPrefix(stagingDir, ct);
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

    private static async IAsyncEnumerable<SourceRecord> FilterAfterCutoff(
        IAsyncEnumerable<SourceRecord> upstream,
        long cutoff,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var r in upstream.WithCancellation(ct))
        {
            if (r.TsMs > cutoff)
                yield return r;
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
