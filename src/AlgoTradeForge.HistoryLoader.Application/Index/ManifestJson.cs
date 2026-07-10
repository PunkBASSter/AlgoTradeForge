using System.Text.Json;

namespace AlgoTradeForge.HistoryLoader.Application.Index;

/// <summary>Matches FeedSchemaManager's on-disk camelCase so manifest_json round-trips FeedMetadata.</summary>
public static class ManifestJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
