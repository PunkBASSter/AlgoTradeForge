using System.Globalization;
using System.Text.Json;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Aggregation;
using AlgoTradeForge.HistoryLoader.Application.Aggregation.Jobs;
using AlgoTradeForge.HistoryLoader.Application.Catalog;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace AlgoTradeForge.HistoryLoader.WebApi.Endpoints;

internal static class AggregationEndpoints
{
    public static WebApplication MapAggregationEndpoints(this WebApplication app)
    {
        var v1 = app.MapGroup("/api/v1");

        v1.MapPost("/exchanges/{exchange}/assets/{asset}/aggregate", PostAggregate);
        v1.MapGet("/aggregations/{jobId}", GetSnapshot);
        v1.MapGet("/aggregations/{jobId}/progress", GetProgressSse);
        v1.MapDelete("/aggregations/{jobId}", CancelAggregation);
        v1.MapDelete("/exchanges/{exchange}/assets/{asset}/feeds/{feedId}", DeleteFeed);

        return app;
    }

    /// <summary>Request body for <c>POST /aggregate</c>.</summary>
    public sealed record AggregateRequest(
        string SourceFeedId,
        string TypeCode,
        decimal? Threshold,
        string ThresholdUnit,
        string InputMode,
        string? ConvenienceInput,
        bool OverwriteExisting);

    private static IResult PostAggregate(
        string exchange,
        string asset,
        AggregateRequest body,
        IOptionsMonitor<HistoryLoaderOptions> options,
        IFeedCatalog catalog,
        ISchemaManager schema,
        IAggregationJobRegistry registry,
        IAggregationJobQueue queue,
        IAggregationTickJobQueue tickQueue)
    {
        // Path / input validation (422)
        if (!FeedIdValidator.TryValidatePathComponent(exchange, out var pathErr1))
            return Unprocessable("invalid_path", pathErr1!);
        if (!FeedIdValidator.TryValidatePathComponent(asset, out var pathErr2))
            return Unprocessable("invalid_path", pathErr2!);
        if (!FeedIdValidator.TryValidateSourceFeedId(body.SourceFeedId, out var srcErr))
            return Unprocessable("invalid_source_feed_id", srcErr!);
        if (string.IsNullOrEmpty(body.TypeCode) || !AltBarFeedId.AllowedTypeCodes.Contains(body.TypeCode))
            return Unprocessable("invalid_type_code", $"type_code must be one of: {string.Join(", ", AltBarFeedId.AllowedTypeCodes)}");

        // Resolve configured asset
        var config = options.CurrentValue;
        var assetConfig = config.Assets.FirstOrDefault(a =>
            string.Equals(a.Exchange, exchange, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(AssetPathConvention.DirectoryName(a.Symbol, a.Type), asset, StringComparison.Ordinal));
        if (assetConfig is null)
            return Results.NotFound(new { error = "asset_not_configured", exchange, asset });

        // Source feed eligibility (422)
        var sourceFeed = catalog.GetFeed(exchange, asset, body.SourceFeedId);
        if (sourceFeed is null)
            return Unprocessable("source_feed_not_found", $"source_feed_id '{body.SourceFeedId}' is not present in feeds.json.");

        var assetEntry = catalog.GetAsset(exchange, asset)!;
        var hasCandleExt = assetEntry.Feeds.Any(f =>
            string.Equals(f.Id, "candle-ext", StringComparison.Ordinal));

        var eligibility = EligibilityRules.ForSource(sourceFeed, assetConfig.Type, hasCandleExt);
        if (!eligibility.EligibleTypes.Contains(body.TypeCode))
        {
            var reason = eligibility.IneligibleTypes
                .FirstOrDefault(i => i.Code == body.TypeCode)?.Reason
                ?? $"type {body.TypeCode} not eligible for source {body.SourceFeedId}";
            return Unprocessable("type_ineligible", reason);
        }

        // Threshold-unit family check. Without this, a user could submit unit=quote_asset
        // against an EqV source and the AltBar ordering check below would compare across
        // unit families.
        var implicitUnit = ThresholdResolver.GetImplicitUnit(body.TypeCode);
        if (!string.Equals(body.ThresholdUnit, implicitUnit, StringComparison.Ordinal))
        {
            return Unprocessable("invalid_threshold_unit",
                $"type_code '{body.TypeCode}' requires threshold_unit='{implicitUnit}'; got '{body.ThresholdUnit}'.");
        }

        // Threshold resolution (422 on conversion error)
        var scale = AssetScaleContextFactory.FromDecimalDigits(assetConfig.DecimalDigits);
        ThresholdResolver.Resolved threshold;
        try
        {
            threshold = ThresholdResolver.Resolve(
                body.ThresholdUnit,
                body.InputMode,
                body.Threshold,
                body.ConvenienceInput,
                scale);
        }
        catch (ArgumentException ex)
        {
            return Unprocessable("invalid_threshold", ex.Message);
        }

        // AltBar source: enforce strictly-larger threshold ordering. Same-type-family is
        // already enforced by eligibility (EligibleTypes restricted to the source's type code);
        // threshold ordering is requested-threshold-dependent so it lands here.
        var sourceIsAltBar = string.Equals(sourceFeed.Kind, "OHLCV_AltBar", StringComparison.Ordinal);
        AltBarFeedId? sourceParsed = null;
        if (sourceIsAltBar)
        {
            // Parse the source feed-id once; reused below for outcome-source-code derivation.
            if (!AltBarFeedId.TryParse(body.SourceFeedId, out sourceParsed, out var sourceParseErr))
                return Unprocessable("invalid_source_feed_id",
                    $"source_feed_id '{body.SourceFeedId}' is marked as OHLCV_AltBar but does not parse: {sourceParseErr}");

            // Resolve source threshold to the same scaled units the new accumulator uses so the
            // ordering compare is apples-to-apples.
            ThresholdResolver.Resolved sourceThreshold;
            try
            {
                sourceThreshold = ThresholdResolver.Resolve(
                    body.ThresholdUnit,
                    inputMode: "absolute",
                    thresholdValue: sourceParsed!.Threshold.AbsoluteValue,
                    convenienceInput: null,
                    scale: scale);
            }
            catch (ArgumentException ex)
            {
                return Unprocessable("invalid_re_aggregation",
                    $"Failed to resolve source threshold for ordering check: {ex.Message}");
            }

            if (threshold.Scaled <= sourceThreshold.Scaled)
            {
                return Unprocessable("invalid_re_aggregation",
                    $"Re-aggregation threshold must be strictly larger than the source's. " +
                    $"Source '{body.SourceFeedId}' has threshold {sourceParsed.Threshold.ToCanonicalString()} " +
                    $"(scaled {sourceThreshold.Scaled}); requested threshold scaled to {threshold.Scaled}.");
            }
        }

        // Outcome feed-id. For an AltBar source, the outcome's SourceCode is the SOURCE's
        // SourceCode (EqV_1m_1000 + threshold 2000 → EqV_1m_2000), not the full feed-id; the
        // manifest's source.feed records the actual source so the chain stays traceable.
        var outcomeSourceCode = sourceParsed?.SourceCode ?? body.SourceFeedId;
        var outcomeFeedIdRaw = $"{body.TypeCode}_{outcomeSourceCode}_{threshold.FeedIdComponent}";
        if (!FeedIdValidator.TryValidateAltBar(outcomeFeedIdRaw, out var parsed, out var parseErr))
            return Unprocessable("invalid_feed_id", parseErr!);

        var outcomeFeedId = parsed!.FeedId;

        // Active-job conflict check (423) — must precede the 409 on-disk-exists check.
        var active = registry.CheckActiveFeedId(outcomeFeedId);
        if (active is not null)
        {
            return Results.Json(new
            {
                code = "feed_already_locked",
                feed_id = outcomeFeedId,
                existing_job_id = active.JobId,
                existing_job_state = active.State.ToString().ToLowerInvariant(),
            }, statusCode: StatusCodes.Status423Locked);
        }

        // On-disk feed-exists check (409)
        var assetDir = BackfillOrchestrator.ResolveAssetDir(config.DataRoot, assetConfig);
        if (!body.OverwriteExisting)
        {
            var manifest = schema.Load(assetDir);
            if (manifest?.Feeds.ContainsKey(outcomeFeedId) == true)
            {
                return Results.Conflict(new
                {
                    code = "feed_already_exists",
                    feed_id = outcomeFeedId,
                    hint = "Pass overwrite_existing=true to rebuild.",
                });
            }
        }

        // Enqueue (race-protected: TryEnqueue rechecks 423 internally). Tick sources route to a
        // separate queue + worker pool so their I/O load doesn't block CPU-bound time-bar
        // aggregations at the queue head. AltBar sources reuse the time-bar queue but the
        // descriptor's Kind redirects PartitionedSourceReader to aggregated/<feedId>/.
        DataFeedKind sourceKind;
        if (string.Equals(body.SourceFeedId, FeedNames.Ticks, StringComparison.Ordinal))
            sourceKind = DataFeedKind.Tick;
        else if (sourceIsAltBar)
            sourceKind = DataFeedKind.AltBar;
        else
            sourceKind = DataFeedKind.TimeBar;

        var job = new AggregationJob(
            JobId: Guid.NewGuid().ToString("N"),
            Source: new DataFeedDescriptor(config.DataRoot, exchange, asset, body.SourceFeedId, sourceKind),
            AssetDir: assetDir,
            OutcomeFeedId: outcomeFeedId,
            TypeCode: body.TypeCode,
            ThresholdAbsolute: threshold.Absolute,
            ThresholdScaled: threshold.Scaled,
            ThresholdUnit: body.ThresholdUnit,
            ThresholdInputMode: body.InputMode,
            ThresholdConvenienceInput: threshold.PreservedConvenienceInput,
            SourceScale: scale,
            AccumulatorScale: scale,
            MaxPartitionSizeMB: config.Aggregator.MaxPartitionSizeMB,
            ToolVersion: typeof(AggregationEndpoints).Assembly.GetName().Version?.ToString() ?? "0.0.0");

        IAggregationJobQueue targetQueue = sourceKind == DataFeedKind.Tick ? tickQueue : queue;
        var outcome = registry.TryEnqueue(job, targetQueue);
        return outcome switch
        {
            EnqueueOutcome.Accepted accepted => AcceptedResult(accepted.Record),
            EnqueueOutcome.FeedAlreadyLocked locked => Results.Json(new
            {
                code = "feed_already_locked",
                feed_id = outcomeFeedId,
                existing_job_id = locked.ExistingJobId,
                existing_job_state = locked.ExistingState.ToString().ToLowerInvariant(),
            }, statusCode: StatusCodes.Status423Locked),
            EnqueueOutcome.QueueFull => Results.Json(
                new { code = "queue_full", retry_after_seconds = 5 },
                statusCode: StatusCodes.Status503ServiceUnavailable),
            _ => Results.Problem("unknown enqueue outcome"),
        };
    }

    private static IResult AcceptedResult(AggregationJobRecord record)
    {
        var location = $"/api/v1/aggregations/{record.Job.JobId}/progress";
        var inner = Results.Json(
            new { job_id = record.Job.JobId, state = record.State.ToString().ToLowerInvariant() },
            statusCode: StatusCodes.Status202Accepted);
        return new AcceptedWithHeaders(record.Job.JobId, location, inner);
    }

    /// <summary>
    /// Wraps an <see cref="IResult"/> with <c>Location</c> + <c>X-Job-Id</c> headers — minimal
    /// API <see cref="Results.Json"/> has no direct header hook.
    /// </summary>
    private sealed class AcceptedWithHeaders(string jobId, string location, IResult inner) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.Location = location;
            httpContext.Response.Headers["X-Job-Id"] = jobId;
            await inner.ExecuteAsync(httpContext);
        }
    }

    private static IResult DeleteFeed(
        string exchange,
        string asset,
        string feedId,
        IOptionsMonitor<HistoryLoaderOptions> options,
        ISchemaManager schema,
        IAggregationJobRegistry registry)
    {
        if (!FeedIdValidator.TryValidatePathComponent(exchange, out var pathErr1))
            return Unprocessable("invalid_path", pathErr1!);
        if (!FeedIdValidator.TryValidatePathComponent(asset, out var pathErr2))
            return Unprocessable("invalid_path", pathErr2!);
        if (!FeedIdValidator.TryValidateAltBar(feedId, out _, out var feedErr))
            return Unprocessable("invalid_feed_id", feedErr!);

        var config = options.CurrentValue;
        var assetConfig = config.Assets.FirstOrDefault(a =>
            string.Equals(a.Exchange, exchange, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(AssetPathConvention.DirectoryName(a.Symbol, a.Type), asset, StringComparison.Ordinal));
        if (assetConfig is null)
            return Results.NotFound(new { error = "asset_not_configured", exchange, asset });

        var assetDir = BackfillOrchestrator.ResolveAssetDir(config.DataRoot, assetConfig);
        var manifest = schema.Load(assetDir);
        if (manifest is null || !manifest.Feeds.TryGetValue(feedId, out var def))
            return Results.NotFound(new { error = "feed_not_found", feed_id = feedId });

        // Only OHLCV_AltBar feeds are user-deletable. Time bars / ticks / side feeds are
        // collector-managed and cannot be removed via this endpoint.
        if (!string.Equals(def.Kind, "OHLCV_AltBar", StringComparison.Ordinal))
        {
            return Results.Json(new
            {
                code = "kind_not_deletable",
                feed_id = feedId,
                kind = def.Kind ?? "OHLCV_TimeBar",
                message = "Only OHLCV_AltBar feeds may be deleted via this endpoint.",
            }, statusCode: StatusCodes.Status403Forbidden);
        }

        // Race guard: a job currently aggregating into this feedId would re-write the manifest
        // entry milliseconds after we delete it. 423 must precede any disk mutation.
        var active = registry.CheckActiveFeedId(feedId);
        if (active is not null)
        {
            return Results.Json(new
            {
                code = "feed_already_locked",
                feed_id = feedId,
                existing_job_id = active.JobId,
                existing_job_state = active.State.ToString().ToLowerInvariant(),
            }, statusCode: StatusCodes.Status423Locked);
        }

        // Delete on-disk (rename-aside then recursive) first; the manifest write is the atomic
        // anchor so readers see "feed present" or "feed missing", never half. The sweeper picks
        // up any leftover .deleted-<ts> directory on next boot.
        var aggregatedRoot = Path.Combine(assetDir, "aggregated");
        var feedDir = Path.Combine(aggregatedRoot, feedId);
        var sidecarFeedId = def.Sidecar;
        var sidecarDir = sidecarFeedId is not null
            ? Path.Combine(aggregatedRoot, sidecarFeedId)
            : null;

        SafeRecursiveDelete(feedDir);
        if (sidecarDir is not null) SafeRecursiveDelete(sidecarDir);

        if (sidecarFeedId is not null)
            schema.RemoveFeedAndSidecar(assetDir, feedId, sidecarFeedId);
        else
            schema.RemoveFeed(assetDir, feedId);

        return Results.NoContent();
    }

    private static void SafeRecursiveDelete(string dir)
    {
        if (!Directory.Exists(dir)) return;
        // Rename-aside before recursive delete so readers don't see a half-deleted dir; the
        // recursive delete is best-effort (StartupSweepService cleans up leftover .deleted-<ts>).
        var aside = $"{dir}.deleted-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
        Directory.Move(dir, aside);
        try { Directory.Delete(aside, recursive: true); }
        catch (IOException) { /* sweeper covers it */ }
    }

    /// <remarks>
    /// 204 means "cancel observed at the registry" (per-job CTS fired), NOT "run was aborted".
    /// Cooperative cancellation can race: if the pipeline emits its last record between this
    /// call and the worker's next cancellation check, OnCompleted may still win and the SSE
    /// terminal event will be <c>complete</c> rather than <c>cancelled</c>. The FE reconciles
    /// via SSE; the REST status is advisory.
    /// </remarks>
    private static IResult CancelAggregation(string jobId, IAggregationJobRegistry registry)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            return Unprocessable("invalid_job_id", "job_id is required.");

        // Single tri-state call. The earlier Get + TryRequestCancel pattern had a TOCTOU window
        // where a concurrent retention eviction between the two calls surfaced a misleading 409.
        return registry.TryRequestCancel(jobId) switch
        {
            CancelRequestOutcome.Requested => Results.NoContent(),
            CancelRequestOutcome.Unknown =>
                Results.NotFound(new { error = "job_not_found_or_expired", job_id = jobId }),
            CancelRequestOutcome.AlreadyTerminal terminal => Results.Json(new
            {
                code = "job_already_terminal",
                job_id = jobId,
                state = terminal.State.ToString().ToLowerInvariant(),
            }, statusCode: StatusCodes.Status409Conflict),
            _ => Results.Problem("unknown cancel outcome"),
        };
    }

    private static IResult GetSnapshot(string jobId, IAggregationJobRegistry registry)
    {
        var record = registry.Get(jobId);
        if (record is null)
            return Results.NotFound(new { error = "job_not_found_or_expired", job_id = jobId });

        var snap = record.Snapshot();
        return Results.Json(new
        {
            job_id = snap.JobId,
            feed_id = snap.FeedId,
            state = snap.State.ToString().ToLowerInvariant(),
            queued_at = snap.QueuedAt,
            started_at = snap.StartedAt,
            completed_at = snap.CompletedAt,
            queue_position = snap.State == AggregationJobState.Queued ? snap.QueuePosition : (int?)null,
            current_partition = snap.State == AggregationJobState.Running ? snap.CurrentPartition : null,
            bars_emitted = snap.State == AggregationJobState.Running || snap.State.IsTerminal()
                ? snap.BarsEmitted
                : (long?)null,
            // Summary mirrors the SSE `complete` payload (minus type/job_id) for round-trip
            // equivalence with the SSE stream.
            summary = snap.State == AggregationJobState.Complete && snap.Result is not null
                ? CompletePayload(snap.Result)
                : null,
            error = snap.Error is { } err
                ? new { code = err.Code, message = err.Message, retryable = err.Retryable }
                : null,
            cancellation = snap.State == AggregationJobState.Cancelled && snap.CancellationReason is not null
                ? new { reason = snap.CancellationReason, at_utc = snap.CompletedAt }
                : null,
        });
    }

    private static async Task GetProgressSse(
        string jobId,
        HttpContext context,
        IAggregationJobRegistry registry,
        IOptionsMonitor<HistoryLoaderOptions> options,
        TimeProvider clock)
    {
        var record = registry.Get(jobId);
        if (record is null)
        {
            context.Response.StatusCode = StatusCodes.Status410Gone;
            await context.Response.WriteAsJsonAsync(new { error = "job_not_found_or_expired", job_id = jobId });
            return;
        }

        context.Response.Headers[HeaderNames.ContentType] = "text/event-stream";
        context.Response.Headers[HeaderNames.CacheControl] = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        await context.Response.Body.FlushAsync(context.RequestAborted);

        var lastEventId = ParseLastEventId(context);
        var lastSentSeq = lastEventId;

        // Capture-before-drain: snapshot the next-event signal BEFORE reading the events list.
        // AppendEvent adds under the events lock and then swaps the signal, so any event added
        // between our capture and our drain has already fired the captured signal — the next
        // iteration drains it. Capturing after the drain would create a TOCTOU that loses
        // terminal events.
        while (!context.RequestAborted.IsCancellationRequested)
        {
            var nextSignal = record.NextEventSignal;

            // First pass diff'd against lastSentSeq; if the consumer's Last-Event-ID is past
            // the last known event, replay the full log below.
            var fresh = record.EventsAfter(lastSentSeq);
            if (lastSentSeq == lastEventId
                && lastEventId > 0
                && fresh.Count == 0
                && record.LastSequence > 0)
            {
                // Resume requested past the last known event — replay the entire log so the FE
                // never silently misses a state transition.
                fresh = record.EventsAfter(0);
            }

            foreach (var je in fresh)
            {
                await WriteSseFrameAsync(context, je.Sequence, je.Event, context.RequestAborted);
                lastSentSeq = je.Sequence;
                if (je.Event is ProgressEvent.Complete or ProgressEvent.Error or ProgressEvent.Cancelled)
                    return;
            }

            try { await nextSignal.WaitAsync(context.RequestAborted); }
            catch (OperationCanceledException) { return; }
        }
    }

    private static IResult Unprocessable(string code, string message) =>
        Results.Json(new { code, message }, statusCode: StatusCodes.Status422UnprocessableEntity);

    private static int ParseLastEventId(HttpContext context)
    {
        var header = context.Request.Headers["Last-Event-ID"].ToString();
        if (string.IsNullOrEmpty(header)) return 0;
        return int.TryParse(header, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }

    private static async Task WriteSseFrameAsync(
        HttpContext context, int seq, ProgressEvent ev, CancellationToken ct)
    {
        var (eventType, payload) = ev switch
        {
            ProgressEvent.Queued q     => ("queued",    (object)new { job_id = q.JobId, feed_id = q.FeedId, queued_at = q.QueuedAt, queue_position = q.QueuePosition }),
            ProgressEvent.Started s    => ("started",   new { job_id = s.JobId, feed_id = s.FeedId, started_at = s.StartedAt, source_feed_id = s.SourceFeedId }),
            ProgressEvent.Progress p   => ("progress",  new { job_id = p.JobId, current_partition = p.CurrentPartition, bars_emitted = p.BarsEmitted, elapsed_ms = p.ElapsedMs }),
            ProgressEvent.Complete c   => ("complete",  CompletePayload(c.Result)),
            ProgressEvent.Error e      => ("error",     new { job_id = e.JobId, code = e.Code, message = e.Message, retryable = e.Retryable }),
            ProgressEvent.Cancelled cn => ("cancelled", new { job_id = cn.JobId, reason = cn.Reason, at_utc = cn.AtUtc }),
            _ => throw new InvalidOperationException($"Unrecognized progress event: {ev.GetType().Name}"),
        };

        var json = JsonSerializer.Serialize(payload, SseJsonOptions);
        var frame = $"id: {seq}\nevent: {eventType}\ndata: {json}\n\n";
        await context.Response.WriteAsync(frame, ct);
        await context.Response.Body.FlushAsync(ct);
    }

    private static object CompletePayload(AggregationResult r) => new
    {
        job_id = r.JobId,
        feed_id = r.OutcomeFeedId,
        sidecar_feed_id = r.SidecarFeedId,        // null for non-EqI; populated for EqI
        bar_count = r.BarCount,
        partitions_written = r.PartitionsWritten,
        first_bar_ts = r.FirstBarTs,
        last_bar_ts = r.LastBarTs,
        fidelity = new
        {
            actual_overshoot_pct = r.ActualOvershootPct,
            max_overshoot_pct = r.MaxOvershootPct,
            estimated_overshoot_pct = r.EstimatedOvershootPct,
            median_source_record_value = r.MedianSourceRecordValue,
            n_factor = r.NFactor,
        },
        duration_seconds = r.DurationSeconds,
    };

    private static readonly JsonSerializerOptions SseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

}
