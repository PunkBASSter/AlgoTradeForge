namespace AlgoTradeForge.HistoryLoader.Application.Aggregation;

/// <summary>
/// Canonical warning copy for alt-bar features. Centralized so eligibility endpoint, the
/// Data tab form, and the Status card render byte-identical strings — the FE relays the
/// server-provided text verbatim.
/// </summary>
public static class AltBarWarnings
{
    public const string TimeBarEqIProxy =
        "Time-bar EqI uses the taker-buy proxy: it underestimates intra-bar churn. " +
        "Rebuild from `ticks` for magnitude-sensitive use.";
}
