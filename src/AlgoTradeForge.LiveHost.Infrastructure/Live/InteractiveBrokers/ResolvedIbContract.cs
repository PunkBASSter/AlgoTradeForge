namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// Resolved tier: the configured contract plus the runtime conId, localSymbol, and (for futures) the chosen
// front-month expiry from reqContractDetails. LastTradeDate is empty for equities.
internal sealed record ResolvedIbContract(
    IbContract Spec,
    int ConId,
    string LocalSymbol,
    string LastTradeDate);
