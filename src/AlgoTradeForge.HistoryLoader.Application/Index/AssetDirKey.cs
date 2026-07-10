namespace AlgoTradeForge.HistoryLoader.Application.Index;

public static class AssetDirKey
{
    /// <summary>Splits an absolute asset dir into (exchange, dir) relative to dataRoot; null when outside dataRoot.</summary>
    public static (string Exchange, string Dir)? FromPath(string dataRoot, string assetDir)
    {
        var rel = Path.GetRelativePath(Path.GetFullPath(dataRoot), Path.GetFullPath(assetDir));
        if (Path.IsPathRooted(rel)) return null;
        var segments = rel.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2 || segments.Any(s => s == "..")) return null;
        return (segments[0], segments[1]);
    }
}
