namespace AlgoTradeForge.Domain.History;

public interface IBarSource
{
    IAsyncEnumerable<OhlcvBar> GetBarsAsync(
        string assetName,
        DateTimeOffset startTime,
        DateTimeOffset endTime,
        CancellationToken ct = default);
}
