using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Application.Abstractions;

public interface IFeedStatusStore
{
    Task<FeedStatus?> Load(string assetDir, string feedName, string interval, CancellationToken ct = default);
    Task Save(string assetDir, string feedName, string interval, FeedStatus status, CancellationToken ct = default);

    // Atomic read-modify-write: loads current status, applies mutate, writes the result, all under the
    // per-path write lock — so concurrent updaters cannot lose each other's changes. mutate receives
    // null when no status file exists yet and must return the FeedStatus to persist.
    Task Update(string assetDir, string feedName, string interval,
        Func<FeedStatus?, FeedStatus> mutate, CancellationToken ct = default);
}
