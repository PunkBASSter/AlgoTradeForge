namespace AlgoTradeForge.Domain.History;

/// <summary>
/// Categorizes a data feed for path resolution and storage convention (TRD §3.1, §9.5).
/// </summary>
/// <remarks>
/// Phase 4+: unify with the <c>DataFeedSubscription</c> polymorphic hierarchy from TRD §9.2
/// (<c>TimeBarSubscription</c> / <c>AltBarSubscription</c> / <c>TickSubscription</c> /
/// <c>SideFeedSubscription</c>). Kept as an enum + <see cref="DataFeedDescriptor"/>
/// record-struct in Phase 1a because:
/// (a) the loader signature surface is plugin-stable — P0-3 enumerated 24 callsites,
///     all pass <c>Arg.Any&lt;DataFeedDescriptor&gt;()</c> in tests cleanly;
/// (b) path resolution is exhaustive on <c>Kind</c>, giving compile-time switch coverage.
/// Phase 4 either adds a constructor taking a <c>DataFeedSubscription</c> or replaces
/// <see cref="DataFeedDescriptor"/> entirely.
/// </remarks>
public enum DataFeedKind
{
    /// <summary>Time-bar OHLCV (e.g. <c>"1m"</c>, <c>"1h"</c>) under <c>candles/</c>.</summary>
    TimeBar,

    /// <summary>Information-driven alt bar (e.g. <c>"EqV_1m_1000"</c>) under <c>aggregated/&lt;feedId&gt;/</c>.</summary>
    AltBar,

    /// <summary>Tick storage (Phase 2a) under <c>ticks/</c>.</summary>
    Tick,

    /// <summary>Side feed — top-level (e.g. <c>"funding-rate"</c>) or alt-bar sidecar (<c>".flow"</c>).</summary>
    Side,
}

/// <summary>
/// Identifies a feed at the loader boundary. Replaces the prior loose tuple
/// <c>(dataRoot, exchange, symbol, interval)</c> on <see cref="Application.CandleIngestion.IInt64BarLoader"/>.
/// <c>FeedId</c> is the authoritative identifier (e.g. <c>"1m"</c> for time bars,
/// <c>"EqV_1m_1000"</c> for alt bars). <see cref="Kind"/> drives path resolution per
/// TRD §9.3:
/// <list type="bullet">
///   <item><c>TimeBar</c>: <c>{root}/{ex}/{asset}/candles/&lt;YYYY-MM&gt;_{FeedId}.csv</c></item>
///   <item><c>AltBar</c>: <c>{root}/{ex}/{asset}/aggregated/{FeedId}/&lt;YYYY-MM&gt;[.pNN].csv</c></item>
///   <item><c>Tick</c>: <c>{root}/{ex}/{asset}/ticks/&lt;YYYY-MM-DD&gt;.csv</c></item>
///   <item><c>Side</c>: <c>{root}/{ex}/{asset}/{FeedId}/&lt;YYYY-MM&gt;[_interval].csv</c></item>
/// </list>
/// </summary>
public readonly record struct DataFeedDescriptor(
    string DataRoot,
    string Exchange,
    string Asset,
    string FeedId,
    DataFeedKind Kind);
