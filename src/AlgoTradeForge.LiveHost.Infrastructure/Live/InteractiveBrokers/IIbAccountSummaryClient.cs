namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// Account-summary request surface over the shared IB socket. Abstracted so IbAccountFundsSource is
// unit-testable without a real EClientSocket (mirrors IIbMarketDataClient / IIbHistoricalTicksClient).
internal interface IIbAccountSummaryClient
{
    int NextReqId();
    Task<IReadOnlyList<IbAccountSummaryRow>> RegisterAccountSummary(int reqId);
    void RequestAccountSummary(int reqId, string group, string tags);
}

internal readonly record struct IbAccountSummaryRow(string Account, string Tag, string Value, string Currency);
