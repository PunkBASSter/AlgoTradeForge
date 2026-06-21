using AlgoTradeForge.Storage;

namespace AlgoTradeForge.Live.Relay;

public sealed class SegmentUploader(IFileStorage storage, string localRoot, string keyPrefix)
{
    public async Task<int> SweepOnce(CancellationToken ct = default)
    {
        if (!Directory.Exists(localRoot)) return 0;

        int uploaded = 0;
        foreach (var path in Directory.EnumerateFiles(localRoot, "*.atft", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var marker = path + ".uploaded";
            if (File.Exists(marker)) continue;

            var relPath = Path.GetRelativePath(localRoot, path).Replace('\\', '/');
            var key = $"{keyPrefix}/{relPath}";

            var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            await storage.WriteAllBytes(key, bytes, ct).ConfigureAwait(false);

            await File.WriteAllTextAsync(marker, key, ct).ConfigureAwait(false);
            uploaded++;
        }
        return uploaded;
    }
}
