using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Trading;
using AlgoTradeForge.LiveHost.Application.Live;
using AlgoTradeForge.LiveHost.Infrastructure.Live;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live;

public class SessionOrderContextTests
{
    private static readonly CryptoAsset BtcUsdt = CryptoAsset.Create("BTCUSDT", "Binance",
        decimalDigits: 2, minOrderQuantity: 0.00001m, maxOrderQuantity: 9000m, quantityStepSize: 0.00001m);

    [Fact]
    public void Submit_TagsOriginatingSession_OnSharedAccountContext()
    {
        var portfolio = new Portfolio { InitialCash = 100_000_00L };
        portfolio.Initialize();
        var account = new LiveOrderContext(portfolio, new OrderValidator(),
            NullLogger.Instance, Substitute.For<IExchangeOrderClient>());
        account.Start(CancellationToken.None);

        var sessionA = Guid.NewGuid();
        Guid? mappedSession = null;
        account.OrderMapped += (_, sId) => mappedSession = sId;

        IOrderContext facade = new SessionOrderContext(sessionA, account);
        var order = new Order { Id = 0, Asset = BtcUsdt, Side = OrderSide.Buy,
            Type = OrderType.Limit, Quantity = 0.001m, LimitPrice = 5000000L };
        var localId = facade.Submit(order);
        account.RekeyToExchangeId(localId, 99L);

        Assert.Equal(sessionA, mappedSession);
        Assert.Equal(portfolio.Cash, facade.Cash);  // reads delegate to the shared ledger
    }
}
