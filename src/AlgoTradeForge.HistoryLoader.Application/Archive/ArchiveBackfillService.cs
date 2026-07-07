using System.Collections.Concurrent;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.Storage.Threading;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.HistoryLoader.Application.Archive;

public readonly record struct ArchiveProgress(int MonthsDone, int MonthsTotal, string CurrentMonth);

public sealed class ArchiveBackfillService(
    ArchiveMaterializerRegistry registry,
    IMonthCoverageCalculator coverage,
    IFeedStatusStore feedStatusStore,
    ISettingsWriter settingsWriter,
    TimeProvider clock,
    ILogger<ArchiveBackfillService> logger)
{
    // Per-feed gate: prevents the scheduled collector and load-job worker from materializing
    // the same feed concurrently.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();

    private SemaphoreSlim GetGate(string assetDir, string feedName, string interval) =>
        _gates.GetOrAdd($"{assetDir}|{feedName}|{interval}", _ => new SemaphoreSlim(1, 1));

    // Covers CLOSED months intersecting [fromMs, toMs] from the archive for a replenishable feed.
    // Returns the REST-tail start: min(toMs, startOfCurrentMonthMs) after processing, or fromMs
    // unchanged when the feed is not replenishable. The current month is NEVER archive-touched
    // (ownership rule). Partial-edge months are materialized as a superset — idempotent.
    public async Task<long> CoverFromArchive(
        AssetCollectionConfig assetConfig,
        FeedCollectionConfig feedConfig,
        string assetDir,
        long fromMs, long toMs,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken ct = default)
    {
        using var _ = await GetGate(assetDir, feedConfig.Name, feedConfig.Interval).LockAsync(ct);

        // Step 1: resolve materializer; null → feed not replenishable from archive.
        var materializer = registry.Resolve(assetConfig.Exchange, feedConfig.Name, assetConfig.Type);
        if (materializer is null)
            return fromMs;

        // Step 2: candidate months = all closed months intersecting [fromMs, min(toMs, currentMonthStart)).
        var currentMonthStartMs = CurrentMonthStartMs();
        var limit = Math.Min(toMs, currentMonthStartMs);
        var candidates = BuildCandidates(fromMs, limit);
        if (candidates.Count == 0)
            return fromMs;

        // Step 3: load recorded source gaps once — coverage credits them so gap-bearing
        // months don't re-materialize on every invocation.
        var status = await feedStatusStore.Load(assetDir, feedConfig.Name, feedConfig.Interval, ct);
        IReadOnlyList<DataGap> gaps = status?.Gaps ?? [];

        // Steps 3+4: iterate oldest→newest; skip covered months; materialize the rest.
        // Track leading unavailable streak to discover the earliest available date.
        int done = 0;
        bool leadingPhase = true;
        bool cursorAdvanced = false;
        DateOnly? discoveredStart = null;

        logger.LogInformation(
            "Archive backfill for {Symbol}/{Feed}/{Interval}: {Count} candidate month(s)",
            assetConfig.Symbol, feedConfig.Name, feedConfig.Interval, candidates.Count);

        foreach (var (year, month) in candidates)
        {
            // Covered months are already complete; they end the leading-unavailable streak.
            if (await coverage.IsMonthCovered(
                assetDir, feedConfig.Name, feedConfig.Interval, year, month, gaps, ct))
            {
                leadingPhase = false;
                done++;
                progress?.Report(new(done, candidates.Count, $"{year:D4}-{month:D2}"));
                continue;
            }

            var result = await materializer.MaterializeMonth(assetConfig, feedConfig, assetDir, year, month, ct);
            done++;
            progress?.Report(new(done, candidates.Count, $"{year:D4}-{month:D2}"));

            if (!result.AvailableAtSource && leadingPhase)
            {
                cursorAdvanced = true;
                logger.LogDebug(
                    "Archive source has no data for {Symbol}/{Feed} {Year}-{Month:D2} (leading unavailable)",
                    assetConfig.Symbol, feedConfig.Name, year, month);
            }
            else if (result.AvailableAtSource && leadingPhase && cursorAdvanced)
            {
                // First available month after leading unavailability.
                discoveredStart = new DateOnly(year, month, 1);
                leadingPhase = false;
            }
            else
            {
                leadingPhase = false;
            }
        }

        // Step 4: persist discovered history start for configured assets.
        // AppSettingsWriter is a no-op for symbols not in config, so safe to call unconditionally.
        if (discoveredStart.HasValue)
        {
            // Reload to get the actual first data row timestamp written by the materializer;
            // fall back to month-start when the status is missing.
            var refreshed = await feedStatusStore.Load(assetDir, feedConfig.Name, feedConfig.Interval, ct);
            DateOnly persistStart;
            if (refreshed?.FirstTimestamp.HasValue == true)
            {
                var ts = DateTimeOffset.FromUnixTimeMilliseconds(refreshed.FirstTimestamp.Value).UtcDateTime;
                persistStart = new DateOnly(ts.Year, ts.Month, ts.Day);
            }
            else
            {
                persistStart = discoveredStart.Value;
            }
            await settingsWriter.UpdateFeedHistoryStart(
                assetConfig.Symbol, assetConfig.Type,
                feedConfig.Name, feedConfig.Interval,
                persistStart, ct);
        }

        // Step 5: return REST-tail start = min(toMs, currentMonthStart) clamped >= fromMs.
        return Math.Max(fromMs, limit);
    }

    private long CurrentMonthStartMs()
    {
        var now = clock.GetUtcNow();
        return new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero)
            .ToUnixTimeMilliseconds();
    }

    // All closed months whose range intersects [fromMs, limit). Includes partial-edge months
    // (mid-month from or mid-month to) since materializing a superset is idempotent.
    private static List<(int year, int month)> BuildCandidates(long fromMs, long limit)
    {
        var candidates = new List<(int year, int month)>();
        int fromIdx = MonthIndex(fromMs);
        int limitIdx = MonthIndex(limit);

        for (int idx = fromIdx; idx <= limitIdx; idx++)
        {
            if (MonthStart(idx) < limit)
                candidates.Add(MonthPair(idx));
        }

        return candidates;
    }

    private static int MonthIndex(long unixMs)
    {
        var dt = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime;
        return dt.Year * 12 + (dt.Month - 1);
    }

    private static long MonthStart(int monthIndex)
    {
        int year = monthIndex / 12;
        int month = monthIndex % 12 + 1;
        return new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
    }

    private static (int year, int month) MonthPair(int monthIndex) =>
        (monthIndex / 12, monthIndex % 12 + 1);
}
