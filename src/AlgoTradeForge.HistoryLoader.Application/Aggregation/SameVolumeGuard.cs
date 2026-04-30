namespace AlgoTradeForge.HistoryLoader.Application.Aggregation;

/// <summary>
/// Validates that two paths live on the same volume / drive root. NTFS rename / move is
/// only atomic within a single volume — cross-volume moves silently fall back to copy+delete,
/// which breaks every "stage then atomic-rename" guarantee in the aggregator.
/// </summary>
/// <remarks>
/// <para>
/// This is a <em>preventive</em> guard, not a junction-resolver. It catches the most common
/// config-error case (someone explicitly setting a staging path on a different drive letter)
/// by comparing the string-prefix volume root. The default resolver uses
/// <see cref="Path.GetPathRoot(string)"/>, which does NOT follow NTFS junctions or symlinks —
/// a junction crossing volumes will pass this guard and only fail at the underlying
/// <see cref="Directory.Move(string,string)"/> / <see cref="File.Move(string,string,bool)"/>
/// call (which throws <see cref="IOException"/> with "cannot move the file to a different
/// disk drive"). That platform-level failure is the final defense — this guard is the
/// belt over the suspenders.
/// </para>
/// <para>
/// Use at any boundary where staging or temp paths COULD be redirected by config:
/// <see cref="PartitionedSinkWriter"/>'s <c>*.tmp</c> path, <see cref="OverwritePathWriter"/>'s
/// <c>.staging-&lt;jobId&gt;</c> path, etc. Tests inject a custom resolver to simulate
/// cross-volume layout on single-drive CI hosts.
/// </para>
/// </remarks>
public static class SameVolumeGuard
{
    public delegate string? VolumeResolver(string path);

    public static readonly VolumeResolver DefaultResolver =
        path => Path.GetPathRoot(Path.GetFullPath(path));

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> when the two paths resolve to different
    /// volumes; otherwise returns silently.
    /// </summary>
    public static void Ensure(string path1, string path2, VolumeResolver? resolver = null)
    {
        resolver ??= DefaultResolver;

        var root1 = resolver(path1);
        var root2 = resolver(path2);

        if (!string.Equals(root1, root2, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Paths must live on the same volume to support atomic rename. " +
                $"path1='{path1}' (volume='{root1}'), path2='{path2}' (volume='{root2}').");
        }
    }
}
