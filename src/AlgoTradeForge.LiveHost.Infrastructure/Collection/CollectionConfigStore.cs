using System.Text.Json;
using AlgoTradeForge.LiveHost.Application.Collection;
using AlgoTradeForge.Storage;

namespace AlgoTradeForge.LiveHost.Infrastructure.Collection;

public sealed class CollectionConfigStore(IFileStorage storage) : ICollectionConfigStore
{
    internal const string Key = "collection.json";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task<StoredCollectionConfig> Load(CancellationToken ct = default)
    {
        var stored = await storage.ReadWithEtag(Key, ct);
        if (stored is null)
            return new StoredCollectionConfig(new CollectionConfig([]), null);

        var config = JsonSerializer.Deserialize<CollectionConfig>(stored.Content, JsonOpts)
            ?? new CollectionConfig([]);
        return new StoredCollectionConfig(config, stored.ETag);
    }

    public async Task<string> Save(CollectionConfig config, string? expectedETag, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(config, JsonOpts);
        return await storage.WriteIfMatch(Key, json, expectedETag, ct);
    }
}
