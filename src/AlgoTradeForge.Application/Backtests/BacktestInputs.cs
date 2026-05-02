using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy.Subscriptions;

namespace AlgoTradeForge.Application.Backtests;

/// <summary>
/// Explicit input shape for one backtest trial (TRD §9.3): a single ordered list of
/// <see cref="DataFeedSubscription"/> where index 0 is the primary (drives the bar clock)
/// and indices 1+ are side feeds (pulled via <c>IFeedContext</c> at the current primary
/// timestamp).
/// </summary>
public sealed record BacktestInputs
{
    /// <summary>
    /// All feed subscriptions in canonical order. Index 0 is the primary (drives the bar clock);
    /// subsequent entries are side feeds. Must be non-empty.
    /// </summary>
    public IReadOnlyList<DataFeedSubscription> Subscriptions { get; }

    public BacktestInputs(IReadOnlyList<DataFeedSubscription> subscriptions)
    {
        ArgumentNullException.ThrowIfNull(subscriptions);

        if (subscriptions.Count == 0)
            throw new ArgumentException(
                "BacktestInputs requires at least one subscription (the primary).",
                nameof(subscriptions));

        var primary = subscriptions[0];
        if (primary is null)
            throw new ArgumentException("Subscriptions[0] (primary) is null.", nameof(subscriptions));

        if (primary.Role != DataFeedRole.Primary)
            throw new ArgumentException(
                $"Subscriptions[0] must have Role=Primary; got {primary.Role}.",
                nameof(subscriptions));

        var primaryKind = primary.KindOf();
        if (primaryKind == DataFeedKind.Side)
            throw new ArgumentException(
                "Primary subscription cannot be a Side feed — Side feeds do not drive the bar clock.",
                nameof(subscriptions));

        for (var i = 1; i < subscriptions.Count; i++)
        {
            var side = subscriptions[i];
            if (side is null)
                throw new ArgumentException($"Subscriptions[{i}] is null.", nameof(subscriptions));

            if (side.Role != DataFeedRole.Side)
                throw new ArgumentException(
                    $"Subscriptions[{i}] must have Role=Side; got {side.Role}.",
                    nameof(subscriptions));
        }

        Subscriptions = [.. subscriptions];
    }

    public bool Equals(BacktestInputs? other) =>
        other is not null && Subscriptions.SequenceEqual(other.Subscriptions);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var sub in Subscriptions)
            hash.Add(sub);
        return hash.ToHashCode();
    }
}
