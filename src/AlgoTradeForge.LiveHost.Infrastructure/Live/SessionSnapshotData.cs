using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Strategy.Subscriptions;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live;

// Per-session view the venue connector needs to assemble a LiveSessionSnapshot. The dispatcher owns
// the session table + order ledger; the connector owns the transport-side bars + exchange queries.
public readonly record struct SessionSnapshotData(
    IReadOnlyList<DataFeedSubscription> Subscriptions,
    Asset ExecutionAsset,
    string QuoteAsset,
    LiveOrderContext OrderContext);
