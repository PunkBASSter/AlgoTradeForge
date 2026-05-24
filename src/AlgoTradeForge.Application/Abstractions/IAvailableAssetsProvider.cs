namespace AlgoTradeForge.Application.Abstractions;

public interface IAvailableAssetsProvider
{
    Task<IReadOnlyList<AvailableAssetInfo>> GetAvailableAssets(CancellationToken ct = default);
}

public sealed record AvailableAssetInfo(string Exchange, string Symbol, bool IsFutures)
{
    public string LookupName => IsFutures ? $"{Symbol}_PERP" : Symbol;
}
