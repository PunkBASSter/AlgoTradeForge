namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// Real IIbAccountSummaryClient: delegates to the shared socket + wrapper (wired in D1).
// The wrapper accumulates rows and completes the awaiter on accountSummaryEnd.
internal sealed class IbConnectionAccountSummaryClient(
    IbConnection connection, IbWrapper wrapper) : IIbAccountSummaryClient
{
    public int NextReqId() => connection.NextReqId();

    public Task<IReadOnlyList<IbAccountSummaryRow>> RegisterAccountSummary(int reqId) =>
        wrapper.RegisterAccountSummary(reqId);

    public void RequestAccountSummary(int reqId, string group, string tags) =>
        connection.Client.reqAccountSummary(reqId, group, tags);
}
