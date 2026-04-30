using System.Globalization;

namespace AlgoTradeForge.HistoryLoader.Application.Aggregation;

/// <summary>
/// Orchestrates the §4.1 overwrite sequence for an aggregated feed:
/// stage to <c>aggregated/&lt;feedId&gt;/.staging-&lt;jobId&gt;/</c>, atomic-rename the live
/// dir aside, pluck the staged contents into the live slot, then delete the renamed-aside dir.
/// </summary>
/// <remarks>
/// Wired in Phase 1a but not invoked until Phase 1b. The startup sweep
/// (<see cref="AggregatedDirSweeper"/>) cleans any partial state left by a crash:
/// <list type="bullet">
///   <item>Crash before <see cref="Promote"/>: <c>.staging-&lt;jobId&gt;</c> is an orphan
///         subdir under a known feed → swept.</item>
///   <item>Crash mid-<see cref="Promote"/> after the rename-aside but before the pluck:
///         the renamed-aside dir name is not in <c>feeds.json</c> → treated as orphan and swept.</item>
/// </list>
/// </remarks>
public sealed class OverwritePathWriter
{
    private readonly SameVolumeGuard.VolumeResolver? _resolver;

    public OverwritePathWriter(SameVolumeGuard.VolumeResolver? volumeResolver = null)
    {
        _resolver = volumeResolver;
    }

    /// <summary>
    /// Computes the staging directory path for a job. Creates the directory if missing.
    /// </summary>
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
    /// Promotes <paramref name="stagingDir"/> to be the new <paramref name="feedDir"/>.
    /// Sequence:
    /// <list type="number">
    ///   <item>If <paramref name="feedDir"/> exists, atomic-rename it to
    ///         <c>&lt;feedDir&gt;.deleted-&lt;UTC ts&gt;</c>. The staging subdir comes along
    ///         with it.</item>
    ///   <item>Atomic-rename the staging subdir (now living under the renamed-aside dir)
    ///         back to the original <paramref name="feedDir"/> path.</item>
    ///   <item>Delete the renamed-aside dir recursively (non-atomic; sweep covers any failure).</item>
    /// </list>
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
                // Best-effort cleanup. If this fails, an orphan `<feedDir>.deleted-<ts>/`
                // dir is left behind; <see cref="AggregatedDirSweeper"/> reaps it on the
                // next HistoryLoader boot because the suffix is not a registered feed-id
                // and therefore not present in feeds.json.
            }
        }
        else
        {
            // No prior live dir. The "stage inside <feedDir>/" model assumes <feedDir>
            // exists at staging time (PrepareStagingDir creates it). If it's missing here,
            // someone deleted it between PrepareStagingDir and Promote — surface loudly.
            throw new InvalidOperationException(
                $"Cannot promote: feedDir '{feedDir}' does not exist. " +
                $"Was it deleted between PrepareStagingDir and Promote?");
        }
    }

    public static string DeletedDirName(string feedDir, DateTime utcNow) =>
        $"{Path.GetFileName(feedDir)}.deleted-{utcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture)}";
}
