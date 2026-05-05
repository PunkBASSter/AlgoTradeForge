namespace AlgoTradeForge.Domain.Strategy.Subscriptions;

/// <summary>
/// Side feed — top-level (e.g. <c>funding-rate</c>) or alt-bar sidecar (e.g.
/// <c>EqV_1m_500m.flow</c>). Pulled via <c>IFeedContext.TryGetLatest</c>; does not drive
/// the bar clock. <see cref="DataFeedSubscription.Role"/> MUST be <see cref="DataFeedRole.Side"/>.
/// </summary>
public sealed record SideFeedSubscription(
    string AssetName,
    string Exchange,
    DataFeedRole Role,
    string FeedId)
    : DataFeedSubscription(AssetName, Exchange, ValidateRole(Role))
{
    // Validate before forwarding to the base ctor; avoids CS0108 shadowing `new` warning a
    // derived `Role { get; } = ...` would introduce.
    private static DataFeedRole ValidateRole(DataFeedRole role) =>
        role == DataFeedRole.Side
            ? role
            : throw new ArgumentException(
                $"SideFeedSubscription must use DataFeedRole.Side; got {role}.",
                nameof(role));
}
