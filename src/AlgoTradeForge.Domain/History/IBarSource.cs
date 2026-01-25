namespace AlgoTradeForge.Domain.History;

public interface IBarSource
{
    IAsyncEnumerable<OhlcvBar> GetBarsAsync(string assetName, CancellationToken ct = default);
}
