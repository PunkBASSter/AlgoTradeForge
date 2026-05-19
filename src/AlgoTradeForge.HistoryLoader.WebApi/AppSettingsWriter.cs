using System.Text.Json;
using System.Text.Json.Nodes;
using AlgoTradeForge.Storage.Threading;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.Storage;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.HistoryLoader.WebApi;

/// <summary>
/// Persists discovered <c>HistoryStart</c> dates back to <c>appsettings.json</c>. Binds
/// <see cref="LocalFileStorage"/> directly (not <c>IFileStorage</c>) because the binary's
/// content-root <c>appsettings.json</c> is host configuration, not data-root content —
/// "appsettings.json on S3" would be nonsense.
/// </summary>
internal sealed class AppSettingsWriter(
    string appSettingsPath,
    LocalFileStorage storage,
    ILogger<AppSettingsWriter> logger)
    : ISettingsWriter
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task UpdateFeedHistoryStart(string symbol, string assetType,
        string feedName, string feedInterval, DateOnly historyStart, CancellationToken ct = default)
    {
        using var _ = await _gate.LockAsync(ct);
        await UpdateFeedHistoryStartCore(symbol, assetType, feedName, feedInterval, historyStart, ct);
    }

    private async Task UpdateFeedHistoryStartCore(string symbol, string assetType,
        string feedName, string feedInterval, DateOnly historyStart, CancellationToken ct)
    {
        JsonNode? root;
        try
        {
            var json = await storage.ReadAllText(appSettingsPath, ct);
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

        await storage.WriteAllText(appSettingsPath, root!.ToJsonString(WriteOptions), ct: ct);

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
