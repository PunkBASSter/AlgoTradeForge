using System.Text.Json;
using AlgoTradeForge.Storage;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.State;

internal sealed class FeedStatusManager(IFileStorage storage) : IFeedStatusStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<FeedStatus?> Load(string assetDir, string feedName, string interval, CancellationToken ct = default)
    {
        var targetPath = GetStatusPath(assetDir, feedName, interval);
        if (!await storage.Exists(targetPath, ct))
            return null;

        string json;
        try
        {
            json = await storage.ReadAllText(targetPath, ct);
        }
        catch (FileNotFoundException)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<FeedStatus>(json, JsonOptions);
        }
        catch (JsonException)
        {
            // Quarantine legacy pre-PR4a NTFS zero-extension corruption; next Save rebuilds.
            var quarantine = targetPath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            try { await storage.Move(targetPath, quarantine, overwrite: true, ct); } catch { /* best-effort */ }
            return null;
        }
    }

    public async Task Save(string assetDir, string feedName, string interval, FeedStatus status, CancellationToken ct = default)
    {
        var targetPath = GetStatusPath(assetDir, feedName, interval);
        var json = JsonSerializer.Serialize(status, JsonOptions);
        await storage.WriteAllText(targetPath, json, ct: ct);
    }

    private static string GetStatusPath(string assetDir, string feedName, string interval)
        => Path.Combine(assetDir, feedName,
            string.IsNullOrEmpty(interval) ? "status.json" : $"status_{interval}.json");
}
