namespace AlgoTradeForge.Domain.Live;

public interface ILiveConnector : IAsyncDisposable
{
    string AccountName { get; }
    LiveSessionStatus Status { get; }
    int SessionCount { get; }
    Task ConnectAsync(CancellationToken ct = default);
    Task AddSessionAsync(LiveSessionConfig config, CancellationToken ct = default);
    Task RemoveSessionAsync(Guid sessionId, CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
}

public enum LiveSessionStatus
{
    Idle,
    Connecting,
    Running,
    Stopping,
    Stopped,
    Error,
}
