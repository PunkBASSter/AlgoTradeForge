using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.HistoryLoader.Application.Aggregation;

/// <summary>
/// Crash-recovery sweep for the <c>aggregated/</c> directory under one asset (TRD §4.1).
/// Removes:
/// <list type="bullet">
///   <item>Every <c>*.tmp</c> file under any <c>aggregated/&lt;feedId&gt;/</c> or
///         <c>aggregated/&lt;feedId&gt;.flow/</c>.</item>
///   <item>Any <c>aggregated/&lt;feedId&gt;/</c> directory whose <c>feedId</c> is absent
///         from <c>feeds.json</c> (orphan partitions left by an interrupted overwrite).</item>
///   <item>Any <c>.staging-&lt;jobId&gt;/</c> sub-directory inside a known feed (Phase 1a:
///         all are deleted unconditionally; Phase 1b adds a "is the job still running?"
///         check via the in-memory job registry).</item>
/// </list>
/// Asymmetry: a <c>feeds.json</c> entry without a corresponding directory is preserved.
/// Only the directory side is destructive.
/// </summary>
public sealed class AggregatedDirSweeper(
    ISchemaManager schemaManager,
    ILogger<AggregatedDirSweeper> logger)
{
    public void Sweep(string assetDir)
    {
        var aggregatedDir = Path.Combine(assetDir, "aggregated");
        if (!Directory.Exists(aggregatedDir))
            return;

        var metadata = schemaManager.Load(assetDir);
        var knownIds = metadata?.Feeds.Keys.ToHashSet(StringComparer.Ordinal)
                       ?? new HashSet<string>(StringComparer.Ordinal);

        foreach (var feedDir in Directory.EnumerateDirectories(aggregatedDir).ToList())
        {
            var feedDirName = Path.GetFileName(feedDir);

            if (!knownIds.Contains(feedDirName))
            {
                Directory.Delete(feedDir, recursive: true);
                logger.LogWarning(
                    "Startup sweep: deleted orphan aggregated dir {Path} (feedId not in feeds.json)",
                    Path.GetFullPath(feedDir));
                continue;
            }

            // Known feedId — clean *.tmp + .staging-* inside it.
            foreach (var tmpFile in Directory.EnumerateFiles(feedDir, "*.tmp", SearchOption.AllDirectories).ToList())
            {
                File.Delete(tmpFile);
                logger.LogWarning(
                    "Startup sweep: deleted orphan tmp file {Path}",
                    Path.GetFullPath(tmpFile));
            }

            foreach (var stagingDir in Directory.EnumerateDirectories(feedDir, ".staging-*").ToList())
            {
                Directory.Delete(stagingDir, recursive: true);
                logger.LogWarning(
                    "Startup sweep: deleted orphan staging dir {Path}",
                    Path.GetFullPath(stagingDir));
            }
        }
    }
}
