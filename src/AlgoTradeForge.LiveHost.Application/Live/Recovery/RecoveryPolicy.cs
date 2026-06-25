namespace AlgoTradeForge.LiveHost.Application.Live.Recovery;

/// <summary>
/// Per-venue gap policy. On a true gap the coordinator requests backfill and polls every
/// <see cref="PollInterval"/> up to <see cref="BackfillBudget"/>; budget zero == declare immediately.
/// </summary>
public sealed record RecoveryPolicy(TimeSpan BackfillBudget, TimeSpan PollInterval)
{
    public static RecoveryPolicy NoBackfill { get; } = new(TimeSpan.Zero, TimeSpan.Zero);
}
