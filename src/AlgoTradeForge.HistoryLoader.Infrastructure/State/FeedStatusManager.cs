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

    public FeedStatus? Load(string assetDir, string feedName)
    {
        var targetPath = GetStatusPath(assetDir, feedName);
        if (!File.Exists(targetPath))
            return null;

        var json = File.ReadAllText(targetPath);
        return JsonSerializer.Deserialize<FeedStatus>(json, JsonOptions);
    }

    public void Save(string assetDir, string feedName, FeedStatus status)
    {
        var feedDir = Path.Combine(assetDir, feedName);
        Directory.CreateDirectory(feedDir);

        var targetPath = GetStatusPath(assetDir, feedName);
        var tmpPath = targetPath + ".tmp";

        var json = JsonSerializer.Serialize(status, JsonOptions);
        File.WriteAllText(tmpPath, json);
        File.Move(tmpPath, targetPath, overwrite: true);
    }

    private static string GetStatusPath(string assetDir, string feedName)
        => Path.Combine(assetDir, feedName, "status.json");
}
