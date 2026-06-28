namespace AlgoTradeForge.LiveHost.Infrastructure.Live;

// Capacities + cadence for the per-session dispatch core (lifted from BinanceLiveOptions usage).
public sealed record LiveDispatcherOptions(
    int EventQueueCapacity,
    int MarketDataQueueCapacity,
    TimeSpan ReconciliationInterval);
