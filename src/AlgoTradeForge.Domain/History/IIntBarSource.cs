namespace AlgoTradeForge.Domain.History;

public interface IIntBarSource
{
    IAsyncEnumerable<IntBar> GetBarsAsync(
        string assetName,
        DateTimeOffset startTime,
        DateTimeOffset endTime,
        CancellationToken ct = default);
}
