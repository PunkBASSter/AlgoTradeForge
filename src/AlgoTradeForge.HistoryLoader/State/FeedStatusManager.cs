using System.Text.Json;

namespace AlgoTradeForge.HistoryLoader.State;

internal static class FeedStatusManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static FeedStatus? Load(string assetDir, string feedName)
    {
        var targetPath = GetStatusPath(assetDir, feedName);
        if (!File.Exists(targetPath))
            return null;

        var json = File.ReadAllText(targetPath);
        return JsonSerializer.Deserialize<FeedStatus>(json, JsonOptions);
    }

    public static void Save(string assetDir, string feedName, FeedStatus status)
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
