namespace AlgoTradeForge.HistoryLoader.Application.Archive;

public sealed class ArchiveMaterializerRegistry
{
    private readonly ILookup<(string Exchange, string Feed), IArchiveMaterializer> _byKey;

    public ArchiveMaterializerRegistry(IEnumerable<IArchiveMaterializer> materializers) =>
        _byKey = materializers.ToLookup(m => (m.Exchange.ToLowerInvariant(), m.FeedName));

    public IArchiveMaterializer? Resolve(string exchange, string feedName, string assetType) =>
        _byKey[(exchange.ToLowerInvariant(), feedName)].FirstOrDefault(m => m.Supports(assetType));

    public bool IsReplenishable(string exchange, string feedName, string assetType) =>
        Resolve(exchange, feedName, assetType) is not null;
}
