namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// The socket round-trip seam: translate a configured contract, issue reqContractDetails, select a single
// contract (one for STK, front-month for FUT). Faked in unit tests; the real impl
// (IbConnectionContractDetailsClient) drives a live IbConnection.
internal interface IIbContractDetailsClient
{
    Task<ResolvedIbContract> FetchContractDetails(IbContract spec, CancellationToken ct = default);
}
