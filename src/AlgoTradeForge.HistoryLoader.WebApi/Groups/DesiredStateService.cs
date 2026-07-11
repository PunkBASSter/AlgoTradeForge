using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Application.Groups;
using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Domain.Symbology;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.HistoryLoader.WebApi.Groups;

internal sealed class DesiredStateService(
    IGroupStore store,
    ConvergenceEvaluator evaluator,
    SymbologyRegistry registry,
    IHistoryIndex index,
    IInstrumentMetaProvider metaProvider,
    CollectionPlanHolder holder,
    IEagerBackfillRunner runner,
    CollectionChangeNotifier notifier,
    ILogger<DesiredStateService> logger) : BackgroundService
{
    private volatile ConvergenceReport? _report;
    private CancellationTokenSource? _debounceCts;
    private readonly object _lock = new();
    private CancellationToken _stopping;

    // key: "{exchange}|{dir}", value: ordered "{feedName}|{interval}|{covered}/{expected}" join of the
    // asset's missing/partial eager non-derived tuples. Mutated ONLY on the debounced pipeline path.
    // Single-flight holds because every Retrigger cancels the prior debounce CTS under _lock, and a
    // pipeline reaches its synchronous kick/publish tail only if it completed all its awaits
    // uncancelled — so two pipelines never mutate this concurrently. An await-free trigger path
    // (running the pipeline without the debounce CTS) would break this invariant. The kick task
    // never touches the dictionary; a group edit requests a clear via _clearFingerprints, consumed
    // on that same pipeline path — no cross-thread dictionary access.
    private readonly Dictionary<string, string> _kickFingerprints = new();
    private int _clearFingerprints;

    // Distinguishes a real shutdown from an HttpClient timeout — the latter throws
    // TaskCanceledException (an OCE) WITHOUT caller cancellation; a naive
    // `ex is not OperationCanceledException` filter lets timeouts silently kill the pipeline.
    private static bool IsTrueShutdown(Exception ex, CancellationToken ct) =>
        ex is OperationCanceledException oce
        && ct.IsCancellationRequested
        && oce.CancellationToken == ct;

    public ConvergenceReport? LatestReport => _report;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;

        // First compute runs unconditionally — LegacyImportService has already written any
        // groups to disk at this point, so the expansion sees them without a startup race.
        await RunPipelineSafe(stoppingToken);

        // Subscribe AFTER the first compute so events only trigger recomputes.
        store.GroupsChanged += OnGroupsChanged;
        notifier.DiscoveryRecorded += Retrigger;

        // Hosted-service StartAsync doesn't await ExecuteAsync ⇒ LegacyImportService's Puts may
        // fire during the first-compute window before the subscription above. Self-trigger one
        // debounced recompute to cover any events missed in that gap. Retrigger (not OnGroupsChanged):
        // fingerprints from the boot sweep stay — a missed Put still kicks via its absent fingerprint,
        // and an unchanged state must not re-kick.
        Retrigger();

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // normal shutdown
        }
        finally
        {
            store.GroupsChanged -= OnGroupsChanged;
            notifier.DiscoveryRecorded -= Retrigger;
            lock (_lock)
            {
                _debounceCts?.Cancel();
                _debounceCts?.Dispose();
                _debounceCts = null;
            }
        }
    }

    // A group edit is a legitimate retry: drop all fingerprints so every eager hole re-kicks.
    private void OnGroupsChanged()
    {
        Interlocked.Exchange(ref _clearFingerprints, 1);
        Retrigger();
    }

    // Discovery / kick-completion: recompute WITHOUT clearing fingerprints.
    private void Retrigger()
    {
        CancellationToken debounceCt;
        lock (_lock)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
            debounceCt = _debounceCts.Token;
        }
        _ = RunDebounced(debounceCt);
    }

    private async Task RunDebounced(CancellationToken ct)
    {
        try
        {
            await Task.Delay(500, ct);
            await RunPipelineSafe(ct);
        }
        catch (OperationCanceledException)
        {
            // debounced away or shutting down
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "desired-state: recompute failed");
        }
    }

    private async Task RunPipelineSafe(CancellationToken ct)
    {
        try
        {
            await RunPipeline(ct);
        }
        catch (Exception ex) when (IsTrueShutdown(ex, ct))
        {
            throw;   // debounced away / shutting down — RunDebounced swallows it
        }
        catch (Exception ex)
        {
            // Includes stray timeouts (OCE without caller cancellation) — log, never silent-abort.
            logger.LogError(ex, "desired-state: pipeline failed");
        }
    }

    private async Task RunPipeline(CancellationToken ct)
    {
        var docs = await store.List(ct);
        var groups = docs.Select(d => d.Group).ToList();
        var state = GroupExpansion.Expand(groups, registry);

        var exchanges = state.Tuples.Where(t => t.Venue is not null)
            .Select(t => t.Exchange).Distinct(StringComparer.Ordinal).ToList();
        foreach (var exchange in exchanges)
        {
            try
            {
                await metaProvider.EnsureFresh(exchange, ct);
            }
            catch (Exception ex) when (!IsTrueShutdown(ex, ct))
            {
                // stale meta beats no plan — the builder falls back to recorded scale / blocks per asset.
                logger.LogWarning(ex, "desired-state: EnsureFresh failed for {Exchange}", exchange);
            }
        }

        var discovered = await index.ListDiscoveredFirstMonths(ct);
        var meta = await index.ListInstrumentMeta(ct: ct);
        var recordedDigits = new Dictionary<(string, string), int>();
        foreach (var assetRow in await index.ListAssets(ct: ct))
            if (RecordedScale.TryGetDecimalDigits(assetRow.ManifestJson, out var digits))
                recordedDigits[(assetRow.Exchange.ToLowerInvariant(), assetRow.Dir)] = digits;

        var plan = CollectionPlanBuilder.Build(state, discovered, meta, recordedDigits);
        foreach (var warning in plan.Warnings)
            logger.LogWarning("plan: {Exchange}/{Dir}: {Message}", warning.Exchange, warning.Dir, warning.Message);

        _report = await evaluator.Evaluate(groups, ct);

        KickEagerBackfills(plan, _report);

        holder.Publish(plan);   // LAST: consumers must never observe a kick against a stale plan
    }

    private void KickEagerBackfills(CollectionPlan plan, ConvergenceReport? report)
    {
        if (Interlocked.Exchange(ref _clearFingerprints, 0) == 1)
            _kickFingerprints.Clear();

        if (report is null) return;

        var byAsset = report.Tuples
            .Where(t => t.Status is "missing" or "partial" && t.Tuple.Collect == "eager" && !t.Tuple.IsDerived && t.Tuple.Venue is not null)
            .GroupBy(t => (t.Tuple.Exchange, t.Tuple.Venue!.Dir));

        var kicks = new List<(CollectionAsset Asset, List<string> Feeds)>();
        foreach (var group in byAsset)
        {
            var fingerprint = string.Join(";", group
                .OrderBy(t => t.Tuple.FeedName, StringComparer.Ordinal).ThenBy(t => t.Tuple.Interval, StringComparer.Ordinal)
                .Select(t => $"{t.Tuple.FeedName}|{t.Tuple.Interval}|{t.MonthsCovered}/{t.MonthsExpected}"));
            var key = $"{group.Key.Exchange}|{group.Key.Dir}";
            if (_kickFingerprints.TryGetValue(key, out var prev) && prev == fingerprint)
                continue;   // unfillable hole / no movement since last kick — spec §3.4
            _kickFingerprints[key] = fingerprint;

            var asset = plan.Assets.FirstOrDefault(a =>
                a.Exchange == group.Key.Exchange && a.Venue.Dir == group.Key.Dir);
            if (asset is null) { _kickFingerprints.Remove(key); continue; }   // blocked/excluded — do NOT retain the fingerprint, or a later plan appearance with unchanged coverage would be suppressed
            kicks.Add((asset, group.Select(t => t.Tuple.FeedName).Distinct().ToList()));
        }
        if (kicks.Count == 0) return;

        _ = Task.Run(async () =>
        {
            try
            {
                foreach (var (asset, feeds) in kicks)
                    await runner.Run(asset, feeds, _stopping);
            }
            catch (Exception ex) { logger.LogError(ex, "kick backfill failed"); }
            finally { Retrigger(); }   // recompute-on-completion closes the loop (spec §3.4)
        }, _stopping);
    }
}
