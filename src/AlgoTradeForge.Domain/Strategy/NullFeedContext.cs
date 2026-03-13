using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Domain.Strategy;

/// <summary>
/// No-op feed context for strategies with no auxiliary data subscriptions.
/// Follows the same singleton pattern as <see cref="NullEventBus"/> / <see cref="NullDebugProbe"/>.
/// </summary>
public sealed class NullFeedContext : IFeedContext
{
    public static readonly NullFeedContext Instance = new();

    public bool TryGetLatest(string feedKey, out double[] values)
    {
        values = [];
        return false;
    }

    public bool HasNewData(string feedKey) => false;

    public DataFeedSchema GetSchema(string feedKey) =>
        throw new InvalidOperationException(
            $"No feed '{feedKey}' available. Strategy has no auxiliary data subscriptions.");
}
