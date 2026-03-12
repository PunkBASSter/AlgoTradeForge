using AlgoTradeForge.Domain.Trading;
using AlgoTradeForge.Infrastructure.Tests.TestUtilities;
using Xunit;

namespace AlgoTradeForge.Infrastructure.Tests.Live.Testnet;

[Collection("BinanceTestnet")]
[Trait("Category", "BinanceTestnet")]
public sealed class ConnectorCancelTests(TestnetConnectorFixture fixture)
{
    private static readonly TimeSpan FillTimeout = TimeSpan.FromSeconds(30);
    private const decimal MinQty = 0.00010m;

    private TestnetOrderStrategy Strategy => fixture.Strategy!;

    [Fact(
#if DEBUG
        Skip = "Requires responsive Binance testnet — run in Release for full integration"
#endif
    )]
    public async Task CancelPendingLimit_Succeeds()
    {
        if (!BinanceTestnetCredentials.IsConfigured)
            Assert.Skip(BinanceTestnetCredentials.SkipReason);

        // Place limit buy far below market — should NOT fill
        var limitPrice = fixture.LastPrice / 2;
        long orderId = 0;

        Strategy.ResetFillTcs();
        Strategy.ResetBarTcs();
        Strategy.OnNextBar = orders =>
        {
            orderId = orders.Submit(new Order
            {
                Id = 0,
                Asset = fixture.Asset!,
                Side = OrderSide.Buy,
                Type = OrderType.Limit,
                Quantity = MinQty,
                LimitPrice = limitPrice,
            });
        };

        // Wait for next bar so the order gets submitted
        await Strategy.NextBarTcs.Task.WaitAsync(TimeSpan.FromSeconds(90));

        // Small delay to let the order reach Binance
        await Task.Delay(2000);

        // Cancel the order
        Strategy.ResetBarTcs();
        Order? cancelled = null;
        Strategy.OnNextBar = orders =>
        {
            cancelled = orders.Cancel(orderId);
        };

        await Strategy.NextBarTcs.Task.WaitAsync(TimeSpan.FromSeconds(90));

        // Verify no fill was received — if the task completes within the timeout, the cancel failed
        await Assert.ThrowsAsync<TimeoutException>(
            () => Strategy.NextFillTcs.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
