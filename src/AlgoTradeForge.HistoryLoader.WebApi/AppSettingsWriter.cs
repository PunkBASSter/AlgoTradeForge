using System.Text.Json;
using System.Text.Json.Nodes;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.HistoryLoader.WebApi;

internal sealed class AppSettingsWriter(string appSettingsPath, ILogger<AppSettingsWriter> logger)
    : ISettingsWriter
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true
    };

    private readonly Lock _lock = new();

    public void UpdateFeedHistoryStart(string symbol, string assetType,
        string feedName, string feedInterval, DateOnly historyStart)
    {
        lock (_lock)
        {
            UpdateFeedHistoryStartCore(symbol, assetType, feedName, feedInterval, historyStart);
        }
    }

    private void UpdateFeedHistoryStartCore(string symbol, string assetType,
        string feedName, string feedInterval, DateOnly historyStart)
    {
        JsonNode? root;
        try
        {
            var json = File.ReadAllText(appSettingsPath);
            root = JsonNode.Parse(json);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read {Path} for settings update", appSettingsPath);
            return;
        }

        var assets = root?["HistoryLoader"]?["Assets"]?.AsArray();
        if (assets is null)
        {
            logger.LogWarning("No HistoryLoader.Assets array found in {Path}", appSettingsPath);
            return;
        }

        var feedNode = FindFeedNode(assets, symbol, assetType, feedName, feedInterval);
        if (feedNode is null)
        {
            logger.LogWarning(
                "No matching feed found for {Symbol}/{Type}/{Feed}/{Interval} in {Path}",
                symbol, assetType, feedName, feedInterval, appSettingsPath);
            return;
        }

        feedNode["HistoryStart"] = historyStart.ToString("O");

        // Atomic write: write to tmp file, then rename over the original.
        var tmpPath = appSettingsPath + ".tmp";
        File.WriteAllText(tmpPath, root!.ToJsonString(WriteOptions));
        File.Move(tmpPath, appSettingsPath, overwrite: true);

        logger.LogInformation(
            "Persisted HistoryStart={HistoryStart} for {Symbol}/{Type}/{Feed}/{Interval}",
            historyStart, symbol, assetType, feedName, feedInterval);
    }

    private static JsonNode? FindFeedNode(JsonArray assets,
        string symbol, string assetType, string feedName, string feedInterval)
    {
        foreach (var asset in assets)
        {
            if (asset is null) continue;

            var sym = asset["Symbol"]?.GetValue<string>();
            var type = asset["Type"]?.GetValue<string>();

            if (!string.Equals(sym, symbol, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(type, assetType, StringComparison.OrdinalIgnoreCase))
                continue;

            var feeds = asset["Feeds"]?.AsArray();
            if (feeds is null) continue;

            foreach (var feed in feeds)
            {
                if (feed is null) continue;

                var name = feed["Name"]?.GetValue<string>();
                var interval = feed["Interval"]?.GetValue<string>() ?? "";

                if (string.Equals(name, feedName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(interval, feedInterval, StringComparison.OrdinalIgnoreCase))
                {
                    return feed;
                }
            }
        }

        return null;
    }
}
