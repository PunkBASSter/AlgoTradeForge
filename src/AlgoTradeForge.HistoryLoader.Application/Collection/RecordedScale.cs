using System.Text.Json;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Application.Index;

namespace AlgoTradeForge.HistoryLoader.Application.Collection;

public static class RecordedScale
{
    /// <summary>Reads the on-disk candle ScaleFactor (10^digits) from a feeds.json manifest
    /// (assets.manifest_json in the index). Returns false when the manifest has no candle config.</summary>
    public static bool TryGetDecimalDigits(string manifestJson, out int digits)
    {
        digits = 0;
        try
        {
            var meta = JsonSerializer.Deserialize<FeedMetadata>(manifestJson, ManifestJson.Options);
            var scale = meta?.Candles?.ScaleFactor;
            if (scale is null or <= 0m)
                return false;
            digits = (int)Math.Round(Math.Log10((double)scale));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
