namespace AlgoTradeForge.HistoryLoader.Application.Aggregation;

/// <summary>
/// Validates two paths live on the same volume. NTFS rename/move is only atomic within a
/// single volume; cross-volume moves silently fall back to copy+delete, breaking the
/// aggregator's stage-then-rename guarantees. Compares string-prefix volume roots — does
/// NOT follow NTFS junctions/symlinks, so cross-volume junctions still fail at the underlying
/// Move call (the platform error is the final defense; this guard catches the common
/// drive-letter misconfig early). Tests inject a custom resolver to simulate cross-volume
/// layout on single-drive CI hosts.
/// </summary>
public static class SameVolumeGuard
{
    public delegate string? VolumeResolver(string path);

    public static readonly VolumeResolver DefaultResolver =
        path => Path.GetPathRoot(Path.GetFullPath(path));

    /// <summary>Throws when the two paths resolve to different volumes.</summary>
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
