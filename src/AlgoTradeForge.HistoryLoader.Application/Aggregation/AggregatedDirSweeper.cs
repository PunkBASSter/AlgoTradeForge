using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.HistoryLoader.Application.Aggregation;

/// <summary>
/// Crash-recovery sweep for the <c>aggregated/</c> directory of one asset. Deletes orphan
/// <c>*.tmp</c> files, unknown feed directories (not in <c>feeds.json</c>), and
/// <c>.staging-*</c> subdirs. A <c>feeds.json</c> entry without a directory is preserved —
/// only the directory side is destructive.
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

            // Log-only: do NOT auto-delete bare/pNN collisions — which file is correct
            // depends on operator context, and auto-delete would mask a writer/migration bug.
            DetectBareAndPartitionCollisions(feedDir);
        }
    }

    private void DetectBareAndPartitionCollisions(string feedDir)
    {
        var files = Directory.EnumerateFiles(feedDir, "*.csv", SearchOption.TopDirectoryOnly).ToList();
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
}
