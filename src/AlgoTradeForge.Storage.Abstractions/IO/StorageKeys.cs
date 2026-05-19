namespace AlgoTradeForge.Storage;

/// <summary>Single source of truth for storage keys. Build keys here, not by hand-concatenation.</summary>
public static class StorageKeys
{
    public const char Separator = '/';

    public static string Combine(params ReadOnlySpan<string> segments)
    {
        if (segments.Length == 0) return "";
        var trimmed = new string[segments.Length];
        for (var i = 0; i < segments.Length; i++)
            trimmed[i] = segments[i].Trim(Separator, '\\');
        return string.Join(Separator, trimmed);
    }

    public static string CandlePartition(string exchange, string assetDir, int year, int month, string? interval)
    {
        var fileName = interval is null ? $"{year:D4}-{month:D2}.csv" : $"{year:D4}-{month:D2}_{interval}.csv";
        return Combine(exchange, assetDir, "candles", fileName);
    }

    public static string FeedPartition(string exchange, string assetDir, string feedName, int year, int month, string? interval)
    {
        var fileName = interval is null ? $"{year:D4}-{month:D2}.csv" : $"{year:D4}-{month:D2}_{interval}.csv";
        return Combine(exchange, assetDir, feedName, fileName);
    }

    public static string FeedsManifest(string exchange, string assetDir)
        => Combine(exchange, assetDir, "feeds.json");

    public static string FeedStatus(string exchange, string assetDir, string feedName, string? interval)
    {
        var fileName = interval is null ? "status.json" : $"status_{interval}.json";
        return Combine(exchange, assetDir, feedName, fileName);
    }

    public static string DailyTickPartition(string exchange, string assetDir, DateOnly day)
        => Combine(exchange, assetDir, "ticks", $"{day:yyyy-MM-dd}.csv");

    public static string RunFolder(string runFolderName)
        => Combine("runs", runFolderName);

    public static string RunEventsLog(string runFolderName)
        => Combine("runs", runFolderName, "events.jsonl");

    public static string RunMeta(string runFolderName)
        => Combine("runs", runFolderName, "meta.json");
}
