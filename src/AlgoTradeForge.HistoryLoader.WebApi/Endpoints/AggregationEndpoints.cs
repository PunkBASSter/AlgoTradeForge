using AlgoTradeForge.Domain.Aggregation;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Aggregation;
using AlgoTradeForge.HistoryLoader.Application.Catalog;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Application.Jobs;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.WebApi.Aggregation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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
        string? ConvenienceInput);

    // internal for direct endpoint-level testing (InternalsVisibleTo)
    internal static async Task<IResult> PostAggregate(
        string exchange,
        string asset,
        AggregateRequest body,
        IOptionsMonitor<HistoryLoaderOptions> options,
        ICollectionPlanSource planSource,
        IFeedCatalog catalog,
        ISchemaManager schema,
        IHistoryIndex index,
        [FromKeyedServices("aggregation-timebar")] IJobWakeupQueue timeBarWakeup,
        [FromKeyedServices("aggregation-tick")] IJobWakeupQueue tickWakeup,
        CancellationToken ct)
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
        var planAsset = planSource.Current.Assets.FirstOrDefault(a =>
            string.Equals(a.Exchange, exchange, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(a.Venue.Dir, asset, StringComparison.Ordinal));
        if (planAsset is null)
            // wire-compatible error code; "configured" now means "declared in an enabled group"
            return Results.NotFound(new { error = "asset_not_configured", exchange, asset });

        // Source feed eligibility (422)
        var sourceFeed = await catalog.GetFeed(exchange, asset, body.SourceFeedId, ct);
        if (sourceFeed is null)
            return Unprocessable("source_feed_not_found", $"source_feed_id '{body.SourceFeedId}' is not present in feeds.json.");

        var assetEntry = (await catalog.GetAsset(exchange, asset, ct))!;
        var hasCandleExt = assetEntry.Feeds.Any(f =>
            string.Equals(f.Id, "candle-ext", StringComparison.Ordinal));

        var eligibility = EligibilityRules.ForSource(sourceFeed, planAsset.Venue.AssetType, hasCandleExt);
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
        var scale = AssetScaleContextFactory.FromDecimalDigits(planAsset.DecimalDigits);
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

        // The durable feed gate (TryAcquireFeedGate below) is the atomic active-job guard now;
        // the prior in-memory 423 pre-check is folded into the gate's Busy outcome.

        // Existing feed → Continue (202), no_new_data (200), or resume_unsupported (422).
        // Full rebuild = explicit Delete then Aggregate; no in-place overwrite.
        var assetDir = BackfillOrchestrator.ResolveAssetDir(config.DataRoot, planAsset);
        ResumeContext? resume = null;

        DataFeedKind sourceKind;
        if (string.Equals(body.SourceFeedId, FeedNames.Ticks, StringComparison.Ordinal))
            sourceKind = DataFeedKind.Tick;
        else if (sourceIsAltBar)
            sourceKind = DataFeedKind.AltBar;
        else
            sourceKind = DataFeedKind.TimeBar;

        var manifest = await schema.Load(assetDir, ct);
        if (manifest?.Feeds.TryGetValue(outcomeFeedId, out var existing) == true)
        {
            // Legacy feeds (no last_ts) can't be safely continued — Range/Renko drop trailing
            // sub-threshold ticks, so synthesizing from last_bar_ts is wrong. Force rebuild.
            if (existing.Source?.LastTs is null)
            {
                return Results.UnprocessableEntity(new
                {
                    code = "resume_unsupported",
                    feed_id = outcomeFeedId,
                    message = "This feed predates incremental continuation. Delete and re-aggregate to rebuild.",
                });
            }

            if (!long.TryParse(
                    existing.Source.LastTs,
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var lastSrcTs))
            {
                return Results.UnprocessableEntity(new
                {
                    code = "resume_unsupported",
                    feed_id = outcomeFeedId,
                    message = $"Existing feed's source.last_ts '{existing.Source.LastTs}' is not a valid epoch ms.",
                });
            }

            // O(1) tail probe; no_new_data short-circuit if source hasn't advanced.
            var sourceDescriptor = new DataFeedDescriptor(
                config.DataRoot, exchange, asset, body.SourceFeedId, sourceKind);
            var sourceLastTs = SourceTailProbe.GetLastTs(sourceDescriptor);
            if (sourceLastTs is null || sourceLastTs.Value <= lastSrcTs)
            {
                return Results.Ok(new
                {
                    code = "no_new_data",
                    feed_id = outcomeFeedId,
                    last_source_ts = lastSrcTs,
                    last_bar_ts = existing.LastBarTs,
                });
            }

            // Cutoff = lastBarTs - 1 so records ts >= lastBarTs get reconsumed and the
            // trailing bar is re-emitted deterministically. Falls back to lastSrcTs for
            // zero-bar prior runs.
            long cutoffTs = lastSrcTs;
            if (existing.LastBarTs is not null && long.TryParse(
                    existing.LastBarTs,
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var lastBarTs))
            {
                cutoffTs = lastBarTs - 1;
            }

            resume = new ResumeContext(
                LastSourceTsMs: cutoffTs,
                LastBrickClose: existing.Build?.LastBrickClose,
                PriorSpec: BuildSpecFromDefinition(existing));
        }

        // Build the self-contained job. JobId is a placeholder — the durable store assigns the
        // authoritative id in TryAcquireFeedGate and the rehydrator stamps row.Id back on run.
        // Tick sources route to a separate wakeup pool so their I/O load doesn't head-of-line the
        // CPU-bound time-bar pool. AltBar sources reuse the time-bar pool but the descriptor's
        // Kind redirects PartitionedSourceReader to aggregated/<feedId>/.
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
            ToolVersion: typeof(AggregationEndpoints).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            Resume: resume);

        var feedKey = FeedGateKey(exchange, asset, outcomeFeedId);
        var reqJson = AggregationRequestRehydrator.Serialize(job, planAsset.DecimalDigits);
        var outcome = await index.TryAcquireFeedGate("aggregation", feedKey, "{}", reqJson, ct);
        switch (outcome)
        {
            case FeedGateOutcome.Acquired acquired:
                var wakeup = sourceKind == DataFeedKind.Tick ? tickWakeup : timeBarWakeup;
                if (wakeup.TryEnqueue(acquired.JobId))
                    return AcceptedResult(acquired.JobId);
                // Dispatch channel full: drop the just-claimed row so a 503 leaves no phantom job.
                await index.DeleteJob(acquired.JobId, ct);
                return Results.Json(
                    new { code = "queue_full", retry_after_seconds = 5 },
                    statusCode: StatusCodes.Status503ServiceUnavailable);

            case FeedGateOutcome.Busy busy:
                return Results.Json(
                    new { error = "feed_busy", feed_id = outcomeFeedId, active_job_id = busy.ExistingJobId },
                    statusCode: StatusCodes.Status409Conflict);

            default:
                return Results.Problem("unknown feed-gate outcome");
        }
    }

    // Aggregation outcome feeds have no interval, so the gate key trails an empty interval segment,
    // matching the load path's {exchange}|{dir}|{feed}|{interval} shape.
    private static string FeedGateKey(string exchange, string asset, string outcomeFeedId) =>
        $"{exchange}|{asset}|{outcomeFeedId}|";

    private static AltBarFeedSpec BuildSpecFromDefinition(FeedDefinition def) =>
        new(
            Kind: def.Kind ?? "OHLCV_AltBar",
            Columns: def.Columns ?? [],
            Type: def.Type ?? new AggregatedTypeInfo { Code = "?", Name = null },
            Source: def.Source ?? new AggregatedSourceInfo { Feed = "?" },
            Threshold: def.Threshold ?? new ThresholdInfo
            {
                Value = 0m,
                Unit = "base_asset",
                InputMode = "absolute",
                ConvenienceInput = null,
            },
            Build: def.Build ?? new BuildInfo(),
            Fidelity: def.Fidelity ?? new FidelityInfo(),
            FirstBarTs: def.FirstBarTs,
            LastBarTs: def.LastBarTs,
            Sidecar: def.Sidecar);

    private static IResult AcceptedResult(string jobId)
    {
        var location = $"/api/v1/aggregations/{jobId}/progress";
        var inner = Results.Json(
            new { job_id = jobId, state = "queued" },
            statusCode: StatusCodes.Status202Accepted);
        return new AcceptedWithHeaders(jobId, location, inner);
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

    // internal for direct endpoint-level testing (InternalsVisibleTo)
    internal static async Task<IResult> DeleteFeed(
        string exchange,
        string asset,
        string feedId,
        IOptionsMonitor<HistoryLoaderOptions> options,
        ICollectionPlanSource planSource,
        ISchemaManager schema,
        IHistoryIndex index,
        CancellationToken ct)
    {
        if (!FeedIdValidator.TryValidatePathComponent(exchange, out var pathErr1))
            return Unprocessable("invalid_path", pathErr1!);
        if (!FeedIdValidator.TryValidatePathComponent(asset, out var pathErr2))
            return Unprocessable("invalid_path", pathErr2!);
        if (!FeedIdValidator.TryValidateAltBar(feedId, out _, out var feedErr))
            return Unprocessable("invalid_feed_id", feedErr!);

        var config = options.CurrentValue;
        var planAsset = planSource.Current.Assets.FirstOrDefault(a =>
            string.Equals(a.Exchange, exchange, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(a.Venue.Dir, asset, StringComparison.Ordinal));
        if (planAsset is null)
            // wire-compatible error code; "configured" now means "declared in an enabled group"
            return Results.NotFound(new { error = "asset_not_configured", exchange, asset });

        var assetDir = BackfillOrchestrator.ResolveAssetDir(config.DataRoot, planAsset);
        var manifest = await schema.Load(assetDir, ct);
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
        // entry milliseconds after we delete it. The active-job check must precede any disk
        // mutation. The durable store's gate key ({exchange}|{dir}|{feedId}|) is the lock now.
        var feedKey = FeedGateKey(exchange, asset, feedId);
        var jobs = await index.ListJobs("aggregation", null, ct);
        var active = jobs.FirstOrDefault(j =>
            string.Equals(j.FeedKey, feedKey, StringComparison.Ordinal)
            && (j.State == "queued" || j.State == "running"));
        if (active is not null)
        {
            return Results.Json(new
            {
                error = "feed_busy",
                feed_id = feedId,
                active_job_id = active.Id,
                active_job_state = active.State,
            }, statusCode: StatusCodes.Status409Conflict);
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
            await schema.RemoveFeedAndSidecar(assetDir, feedId, sidecarFeedId, ct);
        else
            await schema.RemoveFeed(assetDir, feedId, ct);

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

    // DELETE /aggregations/{jobId}, GET /aggregations/{jobId}, and .../progress are thin aliases
    // over the unified job store (JobEndpoints), mirroring the load path's GET /loads/{jobId}.
    // The rich per-job SSE/snapshot wire shape is retired here; M5 normalizes the envelope.
    private static Task<IResult> CancelAggregation(
        string jobId, IHistoryIndex index, IJobCancellationMap cancels, CancellationToken ct) =>
        JobEndpoints.CancelJob(jobId, index, cancels, ct);

    private static Task<IResult> GetSnapshot(string jobId, IHistoryIndex index, CancellationToken ct) =>
        JobEndpoints.GetJob(jobId, index, ct);

    private static Task GetProgressSse(
        string jobId, HttpContext context, IHistoryIndex index, IJobEventSignal signal) =>
        JobEndpoints.GetJobProgressSse(jobId, context, index, signal);

    private static IResult Unprocessable(string code, string message) =>
        Results.Json(new { code, message }, statusCode: StatusCodes.Status422UnprocessableEntity);
}
