namespace AlgoTradeForge.LiveHost.Infrastructure.Live.Recovery;

public sealed class CatchupOptions
{
    public int WarmupBarCount { get; set; } = 256;
    public TimeSpan BackfillBudget { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);
    public required string RelayKeyPrefix { get; set; }
    public required string DataRoot { get; set; }
}
