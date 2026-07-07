using AlgoTradeForge.HistoryLoader.Application.Archive;
using AlgoTradeForge.HistoryLoader.Application.Archive.Jobs;

namespace AlgoTradeForge.HistoryLoader.WebApi.Collection;

// Deliberately NOT Progress<T>: its Report posts to a captured sync context, making delivery
// order/timing non-deterministic in a BackgroundService. This forwards synchronously.
internal sealed class LoadJobProgress(ILoadJobRegistry registry, string jobId) : IProgress<ArchiveProgress>
{
    public void Report(ArchiveProgress value) =>
        registry.OnProgress(jobId, value.MonthsDone, value.MonthsTotal, value.CurrentMonth);
}
