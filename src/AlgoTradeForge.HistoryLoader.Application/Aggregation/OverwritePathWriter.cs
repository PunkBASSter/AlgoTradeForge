using System.Globalization;

namespace AlgoTradeForge.HistoryLoader.Application.Aggregation;

/// <summary>
/// Orchestrates the overwrite sequence for an aggregated feed: stage to
/// <c>aggregated/&lt;feedId&gt;/.staging-&lt;jobId&gt;/</c>, atomic-rename the live dir aside,
/// pluck the staged contents into the live slot, then delete the renamed-aside dir.
/// Crash-recovery is delegated to <see cref="AggregatedDirSweeper"/>.
/// </summary>
public sealed class OverwritePathWriter
{
    private readonly SameVolumeGuard.VolumeResolver? _resolver;

    public OverwritePathWriter(SameVolumeGuard.VolumeResolver? volumeResolver = null)
    {
        _resolver = volumeResolver;
    }

    /// <summary>Computes the staging directory path for a job. Creates the directory if missing.</summary>
    public string PrepareStagingDir(string feedDir, string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            throw new ArgumentException("jobId required.", nameof(jobId));

        var stagingDir = Path.Combine(feedDir, $".staging-{jobId}");
        SameVolumeGuard.Ensure(stagingDir, feedDir, _resolver);
        Directory.CreateDirectory(stagingDir);
        return stagingDir;
    }

    /// <summary>
    /// Promotes <paramref name="stagingDir"/> to be the new <paramref name="feedDir"/> via
    /// atomic-rename-aside, atomic-rename-back, then recursive delete of the aside dir.
    /// </summary>
    public void Promote(string feedDir, string stagingDir)
    {
        SameVolumeGuard.Ensure(stagingDir, feedDir, _resolver);

        if (!Directory.Exists(stagingDir))
            throw new InvalidOperationException(
                $"Staging dir does not exist: {stagingDir}");

        var stagingName = Path.GetFileName(stagingDir);

        if (Directory.Exists(feedDir))
        {
            var deletedDir = $"{feedDir}.deleted-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
            Directory.Move(feedDir, deletedDir);

            var stagingInsideDeleted = Path.Combine(deletedDir, stagingName);
            Directory.Move(stagingInsideDeleted, feedDir);

            try
            {
                Directory.Delete(deletedDir, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup; the sweeper reaps any leftover .deleted-<ts>/ dir on next boot.
            }
        }
        else
        {
            // PrepareStagingDir creates feedDir; missing here means something deleted it in the gap.
            throw new InvalidOperationException(
                $"Cannot promote: feedDir '{feedDir}' does not exist. " +
                $"Was it deleted between PrepareStagingDir and Promote?");
        }
    }

    public static string DeletedDirName(string feedDir, DateTime utcNow) =>
        $"{Path.GetFileName(feedDir)}.deleted-{utcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture)}";
}
