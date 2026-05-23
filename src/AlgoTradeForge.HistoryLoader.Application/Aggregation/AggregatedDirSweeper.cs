using AlgoTradeForge.Storage;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.HistoryLoader.Application.Aggregation;

/// <summary>
/// Crash-recovery sweep for the <c>aggregated/</c> directory of one asset over
/// <see cref="IFileStorage"/>. Deletes orphan <c>*.tmp</c> files, unknown feed directories
/// (not in <c>feeds.json</c>), and <c>.staging-*</c> subdirs. "Immediate subdirs" are derived
/// from key prefixes since object stores have no real directories — empty subdirs are NOT
/// observable on S3, but the writer never produces an empty feed dir without partitions, so
/// the asymmetry is harmless. A <c>feeds.json</c> entry without a directory is preserved —
/// only the directory side is destructive.
/// </summary>
public sealed class AggregatedDirSweeper(
    IFileStorage storage,
    ISchemaManager schemaManager,
    ILogger<AggregatedDirSweeper> logger)
{
    public async Task Sweep(string assetDir, CancellationToken ct = default)
    {
        var aggregatedDir = Path.Combine(assetDir, "aggregated");

        // Load manifest BEFORE inspecting subdirs so the manifest-vs-disk asymmetry case
        // (entry without corresponding dir) still observes Load — the sweeper is the canonical
        // boundary at which the two views are reconciled. Empty aggregated → no destructive
        // work to do, but Load is still part of the contract.
        var metadata = await schemaManager.Load(assetDir, ct);

        var feedDirs = await EnumerateImmediateSubdirs(aggregatedDir, ct);
        if (feedDirs.Count == 0)
            return;

        var knownIds = metadata?.Feeds.Keys.ToHashSet(StringComparer.Ordinal)
                       ?? new HashSet<string>(StringComparer.Ordinal);

        foreach (var feedDirName in feedDirs)
        {
            var feedDir = Path.Combine(aggregatedDir, feedDirName);

            if (!knownIds.Contains(feedDirName))
            {
                await storage.DeleteByPrefix(feedDir, ct);
                logger.LogWarning(
                    "Startup sweep: deleted orphan aggregated dir {Path} (feedId not in feeds.json)",
                    Path.GetFullPath(feedDir));
                continue;
            }

            // Orphan .tmp files (any depth — atomic-rename interrupt could leave them anywhere
            // under the feed dir, but the LocalFileStorage write-session uses a sibling .tmp so
            // recursive scan stays sound).
            await foreach (var tmpKey in storage.ListKeys(feedDir, suffix: ".tmp", recursive: true, ct))
            {
                await storage.Delete(tmpKey, ct);
                logger.LogWarning(
                    "Startup sweep: deleted orphan tmp file {Path}",
                    Path.GetFullPath(tmpKey));
            }

            // Orphan .staging-* subdirs (immediate children of feedDir only — the staging
            // protocol never nests).
            foreach (var subdir in await EnumerateImmediateSubdirs(feedDir, ct))
            {
                if (!subdir.StartsWith(".staging-", StringComparison.Ordinal)) continue;
                var stagingPath = Path.Combine(feedDir, subdir);
                await storage.DeleteByPrefix(stagingPath, ct);
                logger.LogWarning(
                    "Startup sweep: deleted orphan staging dir {Path}",
                    Path.GetFullPath(stagingPath));
            }

            // Log-only: do NOT auto-delete bare/pNN collisions — which file is correct
            // depends on operator context, and auto-delete would mask a writer/migration bug.
            await DetectBareAndPartitionCollisions(feedDir, ct);
        }
    }

    private async Task DetectBareAndPartitionCollisions(string feedDir, CancellationToken ct)
    {
        var files = new List<string>();
        await foreach (var key in storage.ListKeys(feedDir, suffix: ".csv", recursive: false, ct))
        {
            // Skip nested keys (e.g. .staging-*/2026-04.csv) — the collision rule applies to the
            // promoted top-level partitions only. IFileStorage may return keys relative to its
            // own data root rather than the queried prefix, so strip defensively.
            var rel = StripPrefix(key, feedDir);
            if (rel.Contains('/') || rel.Contains(Path.DirectorySeparatorChar)) continue;
            files.Add(key);
        }

        try
        {
            PartitionFilenameParser.EnsureNoDuplicateMonthPartitions(files);
        }
        catch (InvalidDataException ex)
        {
            logger.LogWarning(
                "Startup sweep: bare-and-partNumbered partition collision in feed dir {Path}. " +
                "Reader will reject reads from this feed until the orphan files are reconciled. {Detail}",
                Path.GetFullPath(feedDir),
                ex.Message);
        }
    }

    private async Task<HashSet<string>> EnumerateImmediateSubdirs(string prefix, CancellationToken ct)
    {
        var subdirs = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var key in storage.ListKeys(prefix, suffix: null, recursive: true, ct))
        {
            var rel = StripPrefix(key, prefix);
            var slash = rel.IndexOfAny(['/', Path.DirectorySeparatorChar]);
            if (slash > 0)
                subdirs.Add(rel.Substring(0, slash));
        }
        return subdirs;
    }

    private static string StripPrefix(string key, string prefix)
    {
        if (key.Length >= prefix.Length &&
            key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return key.Substring(prefix.Length).TrimStart('/', Path.DirectorySeparatorChar);
        }
        return key.TrimStart('/', Path.DirectorySeparatorChar);
    }
}
