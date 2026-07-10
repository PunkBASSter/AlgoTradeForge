namespace AlgoTradeForge.HistoryLoader.Application.Index;

public interface IIndexRebuilder
{
    Task Run(string jobId, CancellationToken ct = default);
}
