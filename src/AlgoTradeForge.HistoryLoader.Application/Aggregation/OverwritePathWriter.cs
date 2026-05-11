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
    /// Append-mode staging: copies pre-cutoff partitions verbatim and truncates the
    /// trailing-month file at <paramref name="priorLastBarTs"/> so the resume run re-emits
    /// the trailing bar.
    /// </summary>
    public AppendStagingPlan PrepareStagingDirForAppend(string feedDir, string jobId, long priorLastBarTs)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            throw new ArgumentException("jobId required.", nameof(jobId));

        var stagingDir = Path.Combine(feedDir, $".staging-{jobId}");
        SameVolumeGuard.Ensure(stagingDir, feedDir, _resolver);
        Directory.CreateDirectory(stagingDir);

        var trailingMonth = DateTimeOffset.FromUnixTimeMilliseconds(priorLastBarTs).UtcDateTime
            .ToString("yyyy-MM", CultureInfo.InvariantCulture);

        var allFiles = Directory
            .EnumerateFiles(feedDir, "*.csv", SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToList();

        // Sticky cross-month: any pNN file forces subsequent months to pre-open at .p01.
        var hasEverOverflowed = allFiles
            .Select(f => PartitionFilenameParser.TryParse(Path.GetFileName(f), out _, out var part) && part is not null)
            .Any(b => b);

        var trailingFile = allFiles
            .Where(f =>
                PartitionFilenameParser.TryParse(Path.GetFileName(f), out var month, out _)
                && string.Equals(month, trailingMonth, StringComparison.Ordinal))
            .LastOrDefault();

        if (trailingFile is null)
            throw new InvalidOperationException(
                $"Append staging: trailing bar ts {priorLastBarTs} maps to month '{trailingMonth}' " +
                $"but no partition file matches that month under '{feedDir}'.");

        foreach (var src in allFiles)
        {
            if (string.Equals(src, trailingFile, StringComparison.Ordinal)) continue;
            var dest = Path.Combine(stagingDir, Path.GetFileName(src));
            File.Copy(src, dest, overwrite: false);
        }

        var trailingFileName = Path.GetFileName(trailingFile);
        var truncatedDest = Path.Combine(stagingDir, trailingFileName);
        var truncatedBytes = TruncateAtCutoff(trailingFile, truncatedDest, priorLastBarTs);

        PartitionFilenameParser.TryParse(trailingFileName, out _, out var trailingPart);

        return new AppendStagingPlan(
            StagingDir: stagingDir,
            ResumeState: new PartitionAppendState(
                MonthKey: trailingMonth,
                PartNumber: trailingPart,
                FileBytes: truncatedBytes,
                HasEverOverflowed: hasEverOverflowed));
    }

    private static long TruncateAtCutoff(string srcPath, string destPath, long cutoffTs)
    {
        using var inFs = new FileStream(srcPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var outFs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var reader = new StreamReader(inFs);
        using var writer = new StreamWriter(outFs) { NewLine = "\n" };

        string? line;
        var firstLine = true;
        while ((line = reader.ReadLine()) is not null)
        {
            if (firstLine)
            {
                writer.WriteLine(line);
                firstLine = false;
                continue;
            }
            if (line.Length == 0) continue;

            var commaIdx = line.IndexOf(',');
            if (commaIdx < 0) continue;
            if (!long.TryParse(
                    line.AsSpan(0, commaIdx),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var ts))
                continue;

            // Strict >= so the trailing bar itself is dropped (resume re-emits it).
            if (ts >= cutoffTs) break;

            writer.WriteLine(line);
        }
        writer.Flush();
        return outFs.Length;
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

public sealed record AppendStagingPlan(
    string StagingDir,
    PartitionAppendState ResumeState);

public sealed record PartitionAppendState(
    string MonthKey,
    int? PartNumber,
    long FileBytes,
    bool HasEverOverflowed);
