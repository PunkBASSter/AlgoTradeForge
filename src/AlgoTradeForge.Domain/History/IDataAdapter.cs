namespace AlgoTradeForge.Domain.History;

public interface IDataAdapter
{
    IAsyncEnumerable<RawCandle> FetchCandlesAsync(
        string symbol,
        TimeSpan interval,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct);
}
