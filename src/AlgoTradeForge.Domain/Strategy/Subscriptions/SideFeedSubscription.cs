namespace AlgoTradeForge.Domain.Strategy.Subscriptions;

/// <summary>
/// Side feed — top-level (e.g. <c>funding-rate</c>) or alt-bar sidecar (e.g.
/// <c>EqV_1m_500m.flow</c>). Resolves to <c>{FeedId}/&lt;YYYY-MM&gt;[_interval].csv</c>
/// at the loader (TRD §9.3, §9.5).
/// </summary>
/// <remarks>
/// Side feeds are pulled via <c>IFeedContext.TryGetLatest(feedKey, out values)</c> at the
/// current Primary timestamp; they do not drive the bar clock. <see cref="DataFeedSubscription.Role"/>
/// MUST be <see cref="DataFeedRole.Side"/> — fail-fast at construction so a misuse like
/// <c>new SideFeedSubscription(..., DataFeedRole.Primary, ...)</c> can't form a nonsensical
/// instance that survives serialization. (Cross-subscription invariants — e.g. "exactly one
/// Primary in a BacktestInputs set" — still live in <c>BacktestPreparer</c>; this is the
/// single-instance invariant that's cheap to enforce here.)
/// </remarks>
public sealed record SideFeedSubscription(
    string AssetName,
    string Exchange,
    DataFeedRole Role,
    string FeedId)
    : DataFeedSubscription(AssetName, Exchange, ValidateRole(Role))
{
    // Validate before forwarding to the base primary-ctor parameter; this avoids shadowing
    // (and the CS0108 `new` warning) that a derived `Role { get; } = ...` would introduce.
    private static DataFeedRole ValidateRole(DataFeedRole role) =>
        role == DataFeedRole.Side
            ? role
            : throw new ArgumentException(
                $"SideFeedSubscription must use DataFeedRole.Side; got {role}.",
                nameof(role));
}
