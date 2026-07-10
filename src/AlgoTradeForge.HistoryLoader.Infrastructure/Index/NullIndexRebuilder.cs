using AlgoTradeForge.HistoryLoader.Application.Index;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Index;

// Placeholder until Task 6 implements the full crawl-and-index rebuild.
internal sealed class NullIndexRebuilder : IIndexRebuilder
{
    public Task Run(string jobId, CancellationToken ct = default) => Task.CompletedTask;
}
