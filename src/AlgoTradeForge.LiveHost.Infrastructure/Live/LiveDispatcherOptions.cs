namespace AlgoTradeForge.LiveHost.Infrastructure.Live;

// Capacities + cadence for the per-session dispatch core (lifted from BinanceLiveOptions usage).
// BufferedReportCapacity bounds the distinct never-mapped order ids whose execution reports are held
// pending an OrderMapped (IB introduces ids — external/reconnect orders — that never map).
public sealed record LiveDispatcherOptions(
    int EventQueueCapacity,
    int MarketDataQueueCapacity,
    TimeSpan ReconciliationInterval,
    int BufferedReportCapacity = 1024);
