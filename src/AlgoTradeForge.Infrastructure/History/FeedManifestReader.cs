using System.Text.Json;
using AlgoTradeForge.Storage;
using AlgoTradeForge.Domain.History;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.Infrastructure.History;

public sealed class FeedManifestReader(
    IFileStorage storage,
    ILogger<FeedManifestReader> logger) : IFeedManifestReader
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<FeedMetadata?> Read(string dataRoot, string exchange, string assetDir, CancellationToken ct = default)
    {
        var path = Path.Combine(dataRoot, exchange, assetDir, "feeds.json");
        if (!await storage.Exists(path, ct)) return null;
        try
        {
            await using var stream = await storage.OpenRead(path, ct);
            return await JsonSerializer.DeserializeAsync<FeedMetadata>(stream, JsonOptions, ct);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            logger.LogWarning(ex, "Unreadable feeds.json at {Path}", path);
            return null;
        }
    }
}
