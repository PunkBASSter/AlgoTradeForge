using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Strategy;

namespace AlgoTradeForge.Infrastructure.History;

/// <summary>
/// Which on-disk time-bar feed to load for a requested timeframe, and whether it must be
/// resampled up to that timeframe after loading.
/// </summary>
public readonly record struct FeedResolution(string LoadFeedId, bool Resample);

/// <summary>
/// Resolves a requested <see cref="TimeFrame"/> to a concrete on-disk time-bar feed +
/// resample decision. One implementation per data-source resampling policy (crypto
/// resample-from-source vs. native-else-divisor for vendor-native archives); the policy
/// is chosen once by <see cref="HistoryFeedResolverFactory"/>. See the
/// <c>oop-first-design</c> skill — this is the reference for that pattern.
/// </summary>
public interface IHistoryFeedResolver
{
    Task<FeedResolution> Resolve(Asset asset, TimeFrame requested, CancellationToken ct = default);
}
