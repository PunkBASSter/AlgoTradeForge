using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Trading;
using AlgoTradeForge.LiveHost.Application.Live;
using AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.InteractiveBrokers;

// Shared builders for IbOrderGateway tests: the AAPL asset/contract, a market-buy request, the gateway
// wiring, and a poll-until helper for the off-pump report lane (the worker emits asynchronously).
internal static class GatewayFixture
{
    public static readonly Asset AaplAsset = new EquityAsset { Name = "AAPL", Exchange = "NASDAQ" };

    // The brief's tests pass `Aapl` in the contract slot — keep it a ResolvedIbContract.
    public static readonly ResolvedIbContract Aapl =
        new(new IbContract("AAPL", IbSecType.Stk, "SMART", "NASDAQ", "USD"), ConId: 265598, LocalSymbol: "AAPL", LastTradeDate: "");

    public static readonly ResolvedIbContract AaplContract = Aapl;

    public static IbOrderRequest MktBuy(decimal qty) =>
        new(Account: "DU1", Action: "BUY", OrderType: "MKT", Quantity: qty, LmtPrice: null, AuxPrice: null);

    public static IbOrderRequest LmtSell(decimal qty, double lmtPrice) =>
        new(Account: "DU1", Action: "SELL", OrderType: "LMT", Quantity: qty, LmtPrice: lmtPrice, AuxPrice: null);

    public static IbOrderGateway Build(FakeIbOrderClient client, IbWrapper wrapper, Action<ExecutionReport> onReport) =>
        new(client, wrapper, onReport, NullLogger<IbOrderGateway>.Instance);

    // Mirrors the production Place signature for the AAPL market-buy used across the tests, supplying the
    // Domain Asset / Side / Type / original quantity the gateway joins to fills.
    public static Task<long> Place(this IbOrderGateway gw, string account, ResolvedIbContract contract,
        IbOrderRequest request, CancellationToken ct) =>
        gw.Place(account, AaplAsset, contract, request, OrderSide.Buy, OrderType.Market, request.Quantity, ct);

    public static async Task WaitForReport(List<ExecutionReport> reports, CancellationToken ct)
    {
        for (var i = 0; i < 100 && reports.Count == 0; i++)
            await Task.Delay(20, ct);
    }

    public static async Task WaitForReportCount(List<ExecutionReport> reports, int count, CancellationToken ct)
    {
        for (var i = 0; i < 100 && reports.Count < count; i++)
            await Task.Delay(20, ct);
    }
}
