using System.Collections.Concurrent;
using System.Text.Json;
using AlgoTradeForge.Storage;
using AlgoTradeForge.Storage.Threading;
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

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _writeGates = new();

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
        // Per-path lock: LocalFileStorage.WriteAllText is atomic against readers (AtomicReplace) but two
        // concurrent same-key writers race on its fixed .tmp (the repair endpoint can write a feed's
        // status while a collector cycle does). Serialize writers here; the storage layer keeps readers safe.
        var gate = _writeGates.GetOrAdd(targetPath, _ => new SemaphoreSlim(1, 1));
        using var _ = await gate.LockAsync(ct);
        await storage.WriteAllText(targetPath, json, ct: ct);
    }

    private static string GetStatusPath(string assetDir, string feedName, string interval)
        => Path.Combine(assetDir, feedName,
            string.IsNullOrEmpty(interval) ? "status.json" : $"status_{interval}.json");
}
