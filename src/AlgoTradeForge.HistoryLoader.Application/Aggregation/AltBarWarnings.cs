namespace AlgoTradeForge.HistoryLoader.Application.Aggregation;

/// <summary>
/// Canonical warning copy for alt-bar features (TRD §10.1). Centralized so the eligibility
/// endpoint, the Phase 3 Data tab form, and the Status card render byte-identical strings —
/// the FE doesn't compose copy from API fragments, it relays whatever the server sends.
/// </summary>
/// <remarks>
/// Wording is pinned by the TRD; changes here must round-trip through a TRD update so the
/// FE's golden snapshots stay aligned. P2b-13 produces this constant; Phase 3 (P3-19)
/// consumes it in the Data tab's yellow banner + Status-card panel.
/// </remarks>
public static class AltBarWarnings
{
    /// <summary>
    /// Time-bar EqI uses the <c>m1_taker_buy_proxy</c> reconstruction (TRD §4 / §6.3) —
    /// the proxy underestimates intra-bar churn relative to a tick-source EqI. Surface this
    /// on both the aggregation form (before submission) and the built feed's Status card
    /// (after the run completes) so users selecting an EqI-from-time-bar primary for a run
    /// know the magnitude limitation.
    /// </summary>
    public const string TimeBarEqIProxy =
        "Time-bar EqI uses the taker-buy proxy: it underestimates intra-bar churn. " +
        "Rebuild from `ticks` for magnitude-sensitive use.";
}
