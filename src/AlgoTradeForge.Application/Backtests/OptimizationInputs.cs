using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy.Subscriptions;

namespace AlgoTradeForge.Application.Backtests;

/// <summary>
/// Feed subscriptions for an optimization run. Each <c>Role=Primary</c> entry is a fan-out
/// candidate; each <c>Role=Side</c> entry is a shared side feed attached to every trial.
/// </summary>
public sealed record OptimizationInputs
{
    public IReadOnlyList<DataFeedSubscription> Subscriptions { get; }

    public OptimizationInputs(IReadOnlyList<DataFeedSubscription> subscriptions)
    {
        ArgumentNullException.ThrowIfNull(subscriptions);

        if (subscriptions.Count == 0)
            throw new ArgumentException(
                "OptimizationInputs requires at least one subscription.",
                nameof(subscriptions));

        var sawPrimary = false;
        for (var i = 0; i < subscriptions.Count; i++)
        {
            var sub = subscriptions[i];
            if (sub is null)
                throw new ArgumentException($"Subscriptions[{i}] is null.", nameof(subscriptions));

            switch (sub.Role)
            {
                case DataFeedRole.Primary:
                    var kind = sub.KindOf();
                    if (kind == DataFeedKind.Side)
                        throw new ArgumentException(
                            $"Subscriptions[{i}] has Role=Primary but Kind=Side — Side feeds do not drive the bar clock.",
                            nameof(subscriptions));
                    sawPrimary = true;
                    break;
                case DataFeedRole.Side:
                    break;
                default:
                    throw new ArgumentException(
                        $"Subscriptions[{i}] has unknown Role={sub.Role}.",
                        nameof(subscriptions));
            }
        }

        if (!sawPrimary)
            throw new ArgumentException(
                "OptimizationInputs requires at least one Role=Primary subscription (the candidate set).",
                nameof(subscriptions));

        Subscriptions = [.. subscriptions];
    }

    public bool Equals(OptimizationInputs? other) =>
        other is not null && Subscriptions.SequenceEqual(other.Subscriptions);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var sub in Subscriptions) hash.Add(sub);
        return hash.ToHashCode();
    }
}
