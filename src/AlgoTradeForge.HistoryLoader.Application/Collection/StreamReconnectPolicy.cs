namespace AlgoTradeForge.HistoryLoader.Application.Collection;

/// <summary>
/// Reconnect attempt counter for WebSocket stream services. A failure after a connection
/// that stayed up for at least <paramref name="stableUptime"/> starts a new series
/// (attempt 1) instead of continuing the old one — the max-attempts cap bounds
/// <em>consecutive</em> failures, not lifetime disconnects.
/// </summary>
public sealed class StreamReconnectPolicy(
    int maxAttempts,
    TimeSpan initialDelay,
    TimeSpan stableUptime,
    TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private int _attempts;
    private DateTimeOffset? _connectedAt;

    public void OnConnected() => _connectedAt = _clock.GetUtcNow();

    public ReconnectDecision OnFailure()
    {
        var wasStable = _connectedAt is { } connectedAt
            && _clock.GetUtcNow() - connectedAt >= stableUptime;
        _connectedAt = null;
        _attempts = wasStable ? 1 : _attempts + 1;

        if (_attempts > maxAttempts)
            return new ReconnectDecision(_attempts, GiveUp: true, TimeSpan.Zero);

        var delay = TimeSpan.FromSeconds(initialDelay.TotalSeconds * Math.Pow(2, _attempts - 1));
        return new ReconnectDecision(_attempts, GiveUp: false, delay);
    }

    public void Reset()
    {
        _attempts = 0;
        _connectedAt = null;
    }
}
