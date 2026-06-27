namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// Real IIbContractDetailsClient: allocates a reqId, registers the awaiter BEFORE issuing the request
// (the IbWrapper ordering contract), reqContractDetails over the shared socket, then selects one contract:
// exactly one for STK, the front month for FUT.
internal sealed class IbConnectionContractDetailsClient(
    IbConnection connection, IbWrapper wrapper, TimeProvider timeProvider) : IIbContractDetailsClient
{
    public async Task<ResolvedIbContract> FetchContractDetails(IbContract spec, CancellationToken ct = default)
    {
        var reqId = connection.NextReqId();
        using var request = wrapper.RegisterContractDetails(reqId);
        connection.Client.reqContractDetails(reqId, spec.ToIbApiContract());
        var details = await request.Completion.WaitAsync(TimeSpan.FromSeconds(15), ct);
        var chosen = Select(spec, details);
        return new ResolvedIbContract(spec, chosen.ConId, chosen.LocalSymbol, chosen.LastTradeDate);
    }

    private IbContractDetailsResult Select(IbContract spec, IReadOnlyList<IbContractDetailsResult> details) =>
        spec.SecType switch
        {
            IbSecType.Fut => FuturesFrontMonthSelector.SelectFrontMonth(
                details, DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime)),
            IbSecType.Stk => details.Count == 1
                ? details[0]
                : throw new InvalidOperationException(
                    $"Expected exactly one STK contract for '{spec.Symbol}', got {details.Count}."),
            _ => throw new ArgumentOutOfRangeException(nameof(spec), spec.SecType, null),
        };
}
