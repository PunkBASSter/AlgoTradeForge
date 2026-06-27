using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.Trading;
using AlgoTradeForge.LiveHost.Application.Live;
using AlgoTradeForge.LiveHost.Infrastructure.Live;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live;

public class AccountTargetTests
{
    private static AccountTarget CreateTarget(IExchangeOrderClient client, out Portfolio portfolio)
    {
        portfolio = new Portfolio { InitialCash = 50_000_00L };
        portfolio.Initialize();
        var ctx = new LiveOrderContext(portfolio, new OrderValidator(), NullLogger.Instance, client);
        ctx.Start(CancellationToken.None);
        return new AccountTarget("acctA", portfolio, ctx, client, NullLogger.Instance);
    }

    [Fact]
    public void OrderContextFor_ReturnsFacade_OverSharedPortfolio()
    {
        var target = CreateTarget(Substitute.For<IExchangeOrderClient>(), out var portfolio);
        var ctx = target.OrderContextFor(Guid.NewGuid());
        Assert.Equal(portfolio.Cash, ctx.Cash);
        Assert.Same(portfolio, target.Portfolio);
    }

    [Fact]
    public async Task DisposeAsync_CancelsOpenOrders_AndIsIdempotent()
    {
        var client = Substitute.For<IExchangeOrderClient>();
        var target = CreateTarget(client, out _);
        target.RegisterSymbol("BTCUSDT");

        await target.DisposeAsync();
        await target.DisposeAsync();   // second dispose is a no-op

        await client.Received(1).CancelAllOpenOrdersAsync("BTCUSDT", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DisposeAsync_NoRegisteredSymbols_DoesNotCancelAll()
    {
        var client = Substitute.For<IExchangeOrderClient>();
        var target = CreateTarget(client, out _);

        await target.DisposeAsync();

        await client.DidNotReceive().CancelAllOpenOrdersAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
