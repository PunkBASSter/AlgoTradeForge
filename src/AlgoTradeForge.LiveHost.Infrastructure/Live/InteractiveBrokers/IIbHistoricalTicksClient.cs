namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// Fetches historical "TRADES" ticks for [fromMs, toMs] via reqHistoricalTicks. Abstracted so the
// backfill requester is unit-testable without a socket or market-data entitlement.
internal interface IIbHistoricalTicksClient
{
    Task<IReadOnlyList<IbHistoricalTick>> FetchTrades(
        ResolvedIbContract contract, long fromMs, long toMs, CancellationToken ct = default);
}
