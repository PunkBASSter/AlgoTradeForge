namespace AlgoTradeForge.Domain.Strategy.Modules.MaxHoldBars;

public sealed class MaxHoldBarsModule(MaxHoldBarsParams parameters)
{
    private readonly bool _enabled = parameters.Enabled;
    private readonly int _maxBars = parameters.MaxBars;

    public bool ShouldClose(long currentTimestampMs, DateTimeOffset groupCreatedAt, long barIntervalMs)
    {
        if (!_enabled)
            return false;

        var elapsedMs = currentTimestampMs - groupCreatedAt.ToUnixTimeMilliseconds();
        var barsHeld = elapsedMs / barIntervalMs;
        return barsHeld >= _maxBars;
    }
}
