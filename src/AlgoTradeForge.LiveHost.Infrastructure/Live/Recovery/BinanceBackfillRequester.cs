using AlgoTradeForge.LiveHost.Application.Live.Recovery;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.Recovery;

/// <summary>
/// Binance gap policy (B): request a REST aggTrade backfill of the gap and poll until the archive
/// covers it or the budget expires. Zero budget (IB-style) short-circuits to false.
/// </summary>
public sealed class BinanceBackfillRequester(IAggTradeBackfillClient client, TimeProvider time) : IBackfillRequester
{
    public async Task<bool> TryBackfill(ReplayRequest context, Discontinuity gap, RecoveryPolicy policy, CancellationToken ct = default)
    {
        if (policy.BackfillBudget <= TimeSpan.Zero) return false;

        var deadline = time.GetUtcNow() + policy.BackfillBudget;
        while (true)
        {
            if (await client.FetchAndArchive(context.Asset.Name, gap.FromTs, gap.ToTs, ct).ConfigureAwait(false))
                return true;
            if (time.GetUtcNow() >= deadline) return false;
            await Task.Delay(policy.PollInterval, time, ct).ConfigureAwait(false);
        }
    }
}
