namespace AlgoTradeForge.Application.Abstractions;

public interface IAvailableAssetsProvider
{
    IReadOnlyList<AvailableAssetInfo> GetAvailableAssets();
}

public sealed record AvailableAssetInfo(string Exchange, string Symbol, bool IsFutures)
{
    public string LookupName => IsFutures ? $"{Symbol}_PERP" : Symbol;
}
