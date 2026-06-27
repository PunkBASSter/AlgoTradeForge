using System.Globalization;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// Real IIbHistoricalTicksClient: pages reqHistoricalTicks forward from `fromMs` until `toMs` is covered.
// IB returns <=1000 ticks/request ascending when startDateTime is given; the second-granular overlap dedup
// lives in HistoricalTickPager (unit-tested). reqIds are drawn from the shared IbConnection allocator so they
// can't collide with live subscription / contract-details ids on the one socket. IBApi vocabulary stops here.
internal sealed class IbConnectionHistoricalTicksClient(
    IbConnection connection, IbWrapper wrapper) : IIbHistoricalTicksClient
{
    private const int PageSize = 1000;

    public Task<IReadOnlyList<IbHistoricalTick>> FetchTrades(
        ResolvedIbContract contract, long fromMs, long toMs, CancellationToken ct = default) =>
        HistoricalTickPager.Collect(
            (startSec, c) => FetchPage(contract, startSec, c), fromMs, toMs, PageSize, ct);

    private async Task<IReadOnlyList<IbHistoricalTick>> FetchPage(
        ResolvedIbContract contract, long startSec, CancellationToken ct)
    {
        var reqId = connection.NextReqId();
        var pending = wrapper.RegisterHistoricalTicks(reqId);
        connection.Client.reqHistoricalTicks(
            reqId, contract.ToIbApiContract(),
            startDateTime: FormatIb(startSec * 1000), endDateTime: "",
            numberOfTicks: PageSize, whatToShow: "TRADES", useRth: 0, ignoreSize: false, miscOptions: null);

        return await pending.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
    }

    // IB historical start/end format: "yyyyMMdd-HH:mm:ss" in UTC.
    private static string FormatIb(long ms) =>
        DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyyMMdd-HH:mm:ss", CultureInfo.InvariantCulture);
}
