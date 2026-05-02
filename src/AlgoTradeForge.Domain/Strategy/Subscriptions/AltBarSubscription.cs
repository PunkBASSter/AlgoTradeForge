namespace AlgoTradeForge.Domain.Strategy.Subscriptions;

/// <summary>
/// Alt-bar subscription (e.g. <c>EqV_1m_500m</c>). Resolves to
/// <c>aggregated/{FeedId}/&lt;YYYY-MM&gt;[.pNN].csv</c> at the loader (TRD §9.3, §9.5).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="FeedId"/> MUST match the §3.3 positional grammar:
/// <c>&lt;TypeCode&gt;_&lt;SourceCode&gt;_&lt;Threshold&gt;</c>, optionally suffixed
/// <c>.flow</c>. Validation lives at the API boundary (<c>POST /api/backtests</c>,
/// <c>POST /api/optimizations</c>) rather than the constructor — keeping the Domain
/// project free of any dependency on <c>AlgoTradeForge.HistoryLoader.Domain</c> where
/// <c>AltBarFeedId.TryParse</c> lives. Tests in <c>AlgoTradeForge.Domain.Tests</c> may
/// reference HistoryLoader.Domain to assert grammar conformance.
/// </para>
/// </remarks>
public sealed record AltBarSubscription(
    string AssetName,
    string Exchange,
    DataFeedRole Role,
    string FeedId)
    : DataFeedSubscription(AssetName, Exchange, Role);
