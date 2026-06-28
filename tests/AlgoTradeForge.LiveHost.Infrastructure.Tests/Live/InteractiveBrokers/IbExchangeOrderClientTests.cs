using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Trading;
using AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.InteractiveBrokers;

public sealed class IbExchangeOrderClientTests
{
    private static readonly EquityAsset Aapl = new() { Name = "AAPL", Exchange = "NASDAQ" };

    private static readonly ResolvedIbContract AaplResolved = new(
        new IbContract("AAPL", IbSecType.Stk, "SMART", "NASDAQ", "USD"),
        ConId: 265598, LocalSymbol: "AAPL", LastTradeDate: "");

    private static IbExchangeOrderClient Build(IIbOrderGateway gateway, string account = "DU1")
    {
        var resolver = Substitute.For<IIbContractResolver>();
        resolver.Resolve(Arg.Any<IbContract>(), Arg.Any<CancellationToken>()).Returns(AaplResolved);
        return new IbExchangeOrderClient(account, Aapl, gateway, resolver);
    }

    [Fact]
    public async Task PlaceOrder_MapsStopToAuxPrice_AccountTagged_ReturnsIdEmptyFills()
    {
        var ct = TestContext.Current.CancellationToken;
        var gateway = Substitute.For<IIbOrderGateway>();
        gateway.Place("DU1", Aapl, AaplResolved, Arg.Any<IbOrderRequest>(),
            OrderSide.Sell, OrderType.Stop, 3m, ct).Returns(777L);
        var client = Build(gateway, "DU1");

        var result = await client.PlaceOrderAsync("AAPL", OrderSide.Sell, OrderType.Stop, 3m,
            price: null, stopPrice: 95.0m, ct);

        Assert.Equal(777L, result.OrderId);
        Assert.Empty(result.Fills);
        await gateway.Received(1).Place("DU1", Aapl, AaplResolved,
            Arg.Is<IbOrderRequest>(r =>
                r.OrderType == "STP" &&
                r.AuxPrice == 95.0 &&
                r.Action == "SELL" &&
                r.Account == "DU1"),
            OrderSide.Sell, OrderType.Stop, 3m, ct);
    }

    [Fact]
    public async Task PlaceOrder_MapsMarketOrder_NoLmtOrAuxPrice()
    {
        var ct = TestContext.Current.CancellationToken;
        var gateway = Substitute.For<IIbOrderGateway>();
        gateway.Place(Arg.Any<string>(), Arg.Any<Asset>(), Arg.Any<ResolvedIbContract>(),
            Arg.Any<IbOrderRequest>(), Arg.Any<OrderSide>(), Arg.Any<OrderType>(),
            Arg.Any<decimal>(), ct).Returns(100L);
        var client = Build(gateway);

        var result = await client.PlaceOrderAsync("AAPL", OrderSide.Buy, OrderType.Market, 10m, ct: ct);

        Assert.Equal(100L, result.OrderId);
        Assert.Empty(result.Fills);
        await gateway.Received(1).Place(Arg.Any<string>(), Arg.Any<Asset>(), Arg.Any<ResolvedIbContract>(),
            Arg.Is<IbOrderRequest>(r =>
                r.OrderType == "MKT" &&
                r.LmtPrice == null &&
                r.AuxPrice == null &&
                r.Action == "BUY"),
            OrderSide.Buy, OrderType.Market, 10m, ct);
    }

    [Fact]
    public async Task PlaceOrder_MapsLimitOrder_LmtPriceSet()
    {
        var ct = TestContext.Current.CancellationToken;
        var gateway = Substitute.For<IIbOrderGateway>();
        gateway.Place(Arg.Any<string>(), Arg.Any<Asset>(), Arg.Any<ResolvedIbContract>(),
            Arg.Any<IbOrderRequest>(), Arg.Any<OrderSide>(), Arg.Any<OrderType>(),
            Arg.Any<decimal>(), ct).Returns(200L);
        var client = Build(gateway);

        var result = await client.PlaceOrderAsync("AAPL", OrderSide.Buy, OrderType.Limit, 5m,
            price: 150.25m, ct: ct);

        Assert.Equal(200L, result.OrderId);
        await gateway.Received(1).Place(Arg.Any<string>(), Arg.Any<Asset>(), Arg.Any<ResolvedIbContract>(),
            Arg.Is<IbOrderRequest>(r =>
                r.OrderType == "LMT" &&
                r.LmtPrice == 150.25 &&
                r.AuxPrice == null &&
                r.Action == "BUY"),
            OrderSide.Buy, OrderType.Limit, 5m, ct);
    }

    [Fact]
    public async Task CancelOrder_DelegatesToGateway()
    {
        var ct = TestContext.Current.CancellationToken;
        var gateway = Substitute.For<IIbOrderGateway>();
        var client = Build(gateway);

        await client.CancelOrderAsync("AAPL", 42L, ct);

        gateway.Received(1).Cancel(42L);
    }

    [Fact]
    public async Task CancelAllOpenOrders_DelegatesToGateway_WithOwnAccount()
    {
        var ct = TestContext.Current.CancellationToken;
        var gateway = Substitute.For<IIbOrderGateway>();
        var client = Build(gateway, "DU7");

        await client.CancelAllOpenOrdersAsync("AAPL", ct); // symbol ignored — IB cancels by id, per account

        await gateway.Received(1).CancelAllOpenOrders("DU7", ct);
    }

    [Fact]
    public async Task PlaceOrder_WrongSymbol_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var gateway = Substitute.For<IIbOrderGateway>();
        var client = Build(gateway);

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            client.PlaceOrderAsync("MSFT", OrderSide.Buy, OrderType.Market, 1m, ct: ct));
    }
}
