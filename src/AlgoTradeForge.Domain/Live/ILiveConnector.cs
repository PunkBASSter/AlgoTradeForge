namespace AlgoTradeForge.Domain.Live;

public interface ILiveConnector : IAsyncDisposable
{
    Guid SessionId { get; }
    LiveSessionStatus Status { get; }
    Task StartAsync(LiveSessionConfig config, CancellationToken ct = default);
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
