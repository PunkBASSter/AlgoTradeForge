using System.Text.Json;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.State;

internal sealed class FeedStatusManager : IFeedStatusStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public FeedStatus? Load(string assetDir, string feedName, string interval)
    {
        var targetPath = GetStatusPath(assetDir, feedName, interval);
        if (!File.Exists(targetPath))
            return null;

        var json = File.ReadAllText(targetPath);
        try
        {
            return JsonSerializer.Deserialize<FeedStatus>(json, JsonOptions);
        }
        catch (JsonException)
        {
            // Status file corrupted (typically NTFS zero-extension after an unclean shutdown:
            // the file exists at expected length but contents are all 0x00 because the data
            // pages never flushed). Quarantine it and let the next Save rebuild from scratch.
            var quarantine = targetPath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            try { File.Move(targetPath, quarantine, overwrite: true); } catch { /* best-effort */ }
            return null;
        }
    }

    public void Save(string assetDir, string feedName, string interval, FeedStatus status)
    {
        var feedDir = Path.Combine(assetDir, feedName);
        Directory.CreateDirectory(feedDir);

        var targetPath = GetStatusPath(assetDir, feedName, interval);
        var tmpPath = targetPath + ".tmp";

        var json = JsonSerializer.Serialize(status, JsonOptions);
        // Flush data to disk (not just to OS cache) before the rename. Without Flush(true),
        // NTFS may commit the file's new length but lose the data on an unclean shutdown,
        // leaving a zero-filled file that crashes the next deserialize with '0x00 invalid start of value'.
        using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var sw = new StreamWriter(fs))
        {
            sw.Write(json);
            sw.Flush();
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmpPath, targetPath, overwrite: true);
    }

    private static string GetStatusPath(string assetDir, string feedName, string interval)
        => Path.Combine(assetDir, feedName,
            string.IsNullOrEmpty(interval) ? "status.json" : $"status_{interval}.json");
}
