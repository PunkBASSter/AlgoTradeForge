namespace AlgoTradeForge.HistoryLoader.Application.Aggregation;

/// <summary>
/// Canonical warning copy for alt-bar features. Centralized so eligibility endpoint, the
/// Data tab form, and the Status card render byte-identical strings — the FE relays the
/// server-provided text verbatim.
/// </summary>
public static class AltBarWarnings
{
    public const string TimeBarEqIProxy =
        "Time-bar EqIV uses the taker-buy proxy: it underestimates intra-bar churn. " +
        "Rebuild from `ticks` for magnitude-sensitive use.";

    public const string TimeBarEqIDProxy =
        "Time-bar EqID uses the per-minute taker-buy-quote sum: it underestimates intra-bar " +
        "imbalance churn. Rebuild from `ticks` for magnitude-sensitive use.";

    public const string TimeBarTibApproximation =
        "Time-bar EqIT uses a count proxy derived from `taker_buy_vol / vol × trade_count` — " +
        "it assumes equal-sized trades within each minute. Rebuild from `ticks` for accurate " +
        "participation imbalance.";
}
