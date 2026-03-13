using System.Text.Json;
using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.HistoryLoader.Storage;

internal sealed class FeedSchemaManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public FeedMetadata? Load(string assetDir)
    {
        var path = FeedsJsonPath(assetDir);
        if (!File.Exists(path))
            return null;

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<FeedMetadata>(json, JsonOptions);
    }

    public void EnsureSchema(
        string assetDir,
        string feedName,
        FeedCollectionConfig feedConfig,
        string[] columns,
        AutoApplyDefinition? autoApply = null)
    {
        var existing = Load(assetDir) ?? new FeedMetadata();

        var updatedFeeds = new Dictionary<string, FeedDefinition>(existing.Feeds)
        {
            [feedName] = new FeedDefinition
            {
                Interval  = feedConfig.Interval,
                Columns   = columns,
                AutoApply = autoApply,
            }
        };

        var updated = new FeedMetadata
        {
            Feeds   = updatedFeeds,
            Candles = existing.Candles,
        };

        AtomicWrite(assetDir, updated);
    }

    public void EnsureCandleConfig(string assetDir, int decimalDigits, string interval)
    {
        var existing = Load(assetDir) ?? new FeedMetadata();

        var multiplier = (decimal)Math.Pow(10, decimalDigits);

        var existingIntervals = existing.Candles?.Intervals ?? [];
        var updatedIntervals  = existingIntervals.Contains(interval)
            ? existingIntervals
            : [..existingIntervals, interval];

        var updated = new FeedMetadata
        {
            Feeds   = existing.Feeds,
            Candles = new CandleConfig
            {
                Multiplier = multiplier,
                Intervals  = updatedIntervals,
            },
        };

        AtomicWrite(assetDir, updated);
    }

    // -------------------------------------------------------------------------

    private static string FeedsJsonPath(string assetDir) =>
        Path.Combine(assetDir, "feeds.json");

    private static void AtomicWrite(string assetDir, FeedMetadata metadata)
    {
        Directory.CreateDirectory(assetDir);

        var targetPath = FeedsJsonPath(assetDir);
        var tmpPath    = targetPath + ".tmp";

        var json = JsonSerializer.Serialize(metadata, JsonOptions);
        File.WriteAllText(tmpPath, json);
        File.Move(tmpPath, targetPath, overwrite: true);
    }
}
