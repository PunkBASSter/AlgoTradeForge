namespace AlgoTradeForge.Domain.History;

public interface IBarSource
{
    IAsyncEnumerable<OhlcvBar> GetBarsAsync(CancellationToken ct = default);
}
