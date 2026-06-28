using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Subscriptions;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live;

internal sealed class LiveSessionEntry
{
    private readonly StrongBox<long> _droppedMarketData = new(0L);

    public Guid SessionId { get; }
    public IInt64BarStrategy Strategy { get; }
    // Concrete AccountTarget: a Binance connector only ever handles Binance targets (its own
    // factory produces them), so narrowing once at construction avoids scattered downcasts.
    public AccountTarget Target { get; }
    public string AccountName { get; }
    public IReadOnlyList<DataFeedSubscription> Subscriptions { get; }
    public Asset ExecutionAsset { get; }
    public string QuoteAsset { get; }

    public Channel<Action> EventQueue { get; }

    // Market data is best-effort: drop the newest item under saturation so a flood
    // never back-pressures or starves the exec queue (fills/orders).
    public Channel<Action> MarketDataQueue { get; }

    public long DroppedMarketDataCount => Interlocked.Read(ref _droppedMarketData.Value);

    public Task? ProcessingTask { get; set; }

    // Stored so the lambda can be removed from OrderMapped on session teardown.
    public Action<long, Guid>? OrderMappedHandler { get; set; }

    // The account-scoped order ledger backing this session (shared across sessions on the
    // same account). Reached via Target for fills/pending-order/reconciliation paths.
    public LiveOrderContext OrderContext => Target.OrderContext;

    public LiveSessionEntry(
        Guid sessionId,
        IInt64BarStrategy strategy,
        AccountTarget target,
        string accountName,
        IReadOnlyList<DataFeedSubscription> subscriptions,
        Asset executionAsset,
        string quoteAsset,
        int eventQueueCapacity,
        int marketDataQueueCapacity,
        ILogger logger)
    {
        SessionId = sessionId;
        Strategy = strategy;
        Target = target;
        AccountName = accountName;
        Subscriptions = subscriptions;
        ExecutionAsset = executionAsset;
        QuoteAsset = quoteAsset;

        EventQueue = Channel.CreateBounded<Action>(
            new BoundedChannelOptions(eventQueueCapacity)
            { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

        var box = _droppedMarketData;
        MarketDataQueue = Channel.CreateBounded<Action>(
            new BoundedChannelOptions(marketDataQueueCapacity)
            { SingleReader = true, FullMode = BoundedChannelFullMode.DropNewest },
            itemDropped: _ =>
            {
                var n = Interlocked.Increment(ref box.Value);
                if ((n & 0x3FF) == 0)
                    logger.LogDebug("Session {SessionId} dropped {Count} market-data callbacks (queue saturated)",
                        sessionId, n);
            });
    }
}
