namespace AlgoTradeForge.LiveHost.Application.Collection;

public interface ICollectionConfigStore
{
    Task<StoredCollectionConfig> Load(CancellationToken ct = default);
    Task<string> Save(CollectionConfig config, string? expectedETag, CancellationToken ct = default);
}

public sealed record StoredCollectionConfig(CollectionConfig Config, string? ETag);
