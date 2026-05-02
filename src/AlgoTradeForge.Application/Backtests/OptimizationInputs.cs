using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy.Subscriptions;

namespace AlgoTradeForge.Application.Backtests;

/// <summary>
/// Explicit input shape for an optimization run (TRD §9.6): a single ordered list of
/// <see cref="DataFeedSubscription"/> where every <c>Role=Primary</c> entry is a fan-out
/// candidate and every <c>Role=Side</c> entry is a shared side feed attached to every
/// trial. Each <c>(primary, parameter combination)</c> pair becomes one trial; per-primary
/// <c>IParameterNormalizer</c> deduplication still applies. Same shape as
/// <see cref="BacktestInputs"/>, scaled up to multiple primaries.
/// </summary>
public sealed record OptimizationInputs
{
    /// <summary>
    /// All feed subscriptions in canonical order. Must contain at least one <c>Role=Primary</c>
    /// entry (the candidate set the engine fans out across). <c>Role=Side</c> entries are
    /// shared side feeds attached to every trial.
    /// </summary>
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
