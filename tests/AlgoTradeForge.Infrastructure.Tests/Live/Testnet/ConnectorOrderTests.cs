using AlgoTradeForge.Domain.Trading;
using AlgoTradeForge.Infrastructure.Tests.TestUtilities;
using Xunit;

namespace AlgoTradeForge.Infrastructure.Tests.Live.Testnet;

[Collection("BinanceTestnet")]
[Trait("Category", "BinanceTestnet")]
public sealed class ConnectorOrderTests(TestnetConnectorFixture fixture)
{
    private static readonly TimeSpan FillTimeout = TimeSpan.FromSeconds(90);
    private const decimal MinQty = 0.00010m;

    private TestnetOrderStrategy Strategy => fixture.Strategy!;
    private long ReferencePrice => fixture.LastPrice;

    [Fact(
#if DEBUG
        Skip = "Waits up to 90s for a real-time bar close — run in Release for full integration"
#endif
    )]
    public async Task KlineStream_ReceivesBar()
    {
        if (!BinanceTestnetCredentials.IsConfigured)
            Assert.Skip(BinanceTestnetCredentials.SkipReason);

        Strategy.ResetBarTcs();
        var bar = await Strategy.NextBarTcs.Task.WaitAsync(TimeSpan.FromSeconds(90));

        Assert.True(bar.Open > 0);
        Assert.True(bar.High >= bar.Low);
        Assert.True(bar.Close > 0);
        Assert.NotEmpty(Strategy.ReceivedBars);
    }

    [Fact]
    public async Task MarketBuy_ReceivesFill()
    {
        if (!BinanceTestnetCredentials.IsConfigured)
            Assert.Skip(BinanceTestnetCredentials.SkipReason);

        Strategy.ResetFillTcs();
        Strategy.OnNextBar = orders =>
        {
            orders.Submit(new Order
            {
                Id = 0,
                Asset = fixture.Asset!,
                Side = OrderSide.Buy,
                Type = OrderType.Market,
                Quantity = MinQty,
            });
        };

        var fill = await Strategy.NextFillTcs.Task.WaitAsync(FillTimeout);

        Assert.Equal(OrderSide.Buy, fill.Side);
        Assert.True(fill.Price > 0);
        Assert.Equal(MinQty, fill.Quantity);
    }

    [Fact]
    public async Task MarketSell_AfterBuy_ReceivesFill()
    {
        if (!BinanceTestnetCredentials.IsConfigured)
            Assert.Skip(BinanceTestnetCredentials.SkipReason);

        // Buy first
        Strategy.ResetFillTcs();
        Strategy.OnNextBar = orders =>
        {
            orders.Submit(new Order
            {
                Id = 0,
                Asset = fixture.Asset!,
                Side = OrderSide.Buy,
                Type = OrderType.Market,
                Quantity = MinQty,
            });
        };
        await Strategy.NextFillTcs.Task.WaitAsync(FillTimeout);

        // Sell
        Strategy.ResetFillTcs();
        Strategy.OnNextBar = orders =>
        {
            orders.Submit(new Order
            {
                Id = 0,
                Asset = fixture.Asset!,
                Side = OrderSide.Sell,
                Type = OrderType.Market,
                Quantity = MinQty,
            });
        };

        var fill = await Strategy.NextFillTcs.Task.WaitAsync(FillTimeout);
        Assert.Equal(OrderSide.Sell, fill.Side);
    }

    [Fact]
    public async Task LimitBuy_AggressivePrice_ReceivesFill()
    {
        if (!BinanceTestnetCredentials.IsConfigured)
            Assert.Skip(BinanceTestnetCredentials.SkipReason);

        var limitPrice = ReferencePrice + (long)(100m / fixture.Asset!.TickSize); // far above market

        Strategy.ResetFillTcs();
        Strategy.OnNextBar = orders =>
        {
            orders.Submit(new Order
            {
                Id = 0,
                Asset = fixture.Asset!,
                Side = OrderSide.Buy,
                Type = OrderType.Limit,
                Quantity = MinQty,
                LimitPrice = limitPrice,
            });
        };

        var fill = await Strategy.NextFillTcs.Task.WaitAsync(FillTimeout);

        Assert.Equal(OrderSide.Buy, fill.Side);
        Assert.True(fill.Price <= limitPrice);
    }

    [Fact]
    public async Task LimitSell_AggressivePrice_ReceivesFill()
    {
        if (!BinanceTestnetCredentials.IsConfigured)
            Assert.Skip(BinanceTestnetCredentials.SkipReason);

        // Buy first to have position
        Strategy.ResetFillTcs();
        Strategy.OnNextBar = orders =>
        {
            orders.Submit(new Order
            {
                Id = 0,
                Asset = fixture.Asset!,
                Side = OrderSide.Buy,
                Type = OrderType.Market,
                Quantity = MinQty,
            });
        };
        await Strategy.NextFillTcs.Task.WaitAsync(FillTimeout);

        var limitPrice = ReferencePrice - (long)(100m / fixture.Asset!.TickSize); // far below market

        Strategy.ResetFillTcs();
        Strategy.OnNextBar = orders =>
        {
            orders.Submit(new Order
            {
                Id = 0,
                Asset = fixture.Asset!,
                Side = OrderSide.Sell,
                Type = OrderType.Limit,
                Quantity = MinQty,
                LimitPrice = limitPrice,
            });
        };

        var fill = await Strategy.NextFillTcs.Task.WaitAsync(FillTimeout);

        Assert.Equal(OrderSide.Sell, fill.Side);
        Assert.True(fill.Price >= limitPrice);
    }

    [Fact]
    public async Task StopBuy_AlreadyTriggered_ReceivesFill()
    {
        if (!BinanceTestnetCredentials.IsConfigured)
            Assert.Skip(BinanceTestnetCredentials.SkipReason);

        var stopPrice = ReferencePrice - (long)(500m / fixture.Asset!.TickSize); // well below market, should trigger immediately

        Strategy.ResetFillTcs();
        try
        {
            Strategy.OnNextBar = orders =>
            {
                orders.Submit(new Order
                {
                    Id = 0,
                    Asset = fixture.Asset!,
                    Side = OrderSide.Buy,
                    Type = OrderType.Stop,
                    Quantity = MinQty,
                    StopPrice = stopPrice,
                });
            };

            var fill = await Strategy.NextFillTcs.Task.WaitAsync(FillTimeout);
            Assert.Equal(OrderSide.Buy, fill.Side);
        }
        catch (HttpRequestException)
        {
            Assert.Skip("Binance testnet rejected STOP_LOSS order — filter restriction.");
        }
    }

    [Fact]
    public async Task StopSell_AlreadyTriggered_ReceivesFill()
    {
        if (!BinanceTestnetCredentials.IsConfigured)
            Assert.Skip(BinanceTestnetCredentials.SkipReason);

        // Buy first
        Strategy.ResetFillTcs();
        Strategy.OnNextBar = orders =>
        {
            orders.Submit(new Order
            {
                Id = 0,
                Asset = fixture.Asset!,
                Side = OrderSide.Buy,
                Type = OrderType.Market,
                Quantity = MinQty,
            });
        };
        await Strategy.NextFillTcs.Task.WaitAsync(FillTimeout);

        var stopPrice = ReferencePrice + (long)(500m / fixture.Asset!.TickSize); // well above market

        Strategy.ResetFillTcs();
        try
        {
            Strategy.OnNextBar = orders =>
            {
                orders.Submit(new Order
                {
                    Id = 0,
                    Asset = fixture.Asset!,
                    Side = OrderSide.Sell,
                    Type = OrderType.Stop,
                    Quantity = MinQty,
                    StopPrice = stopPrice,
                });
            };

            var fill = await Strategy.NextFillTcs.Task.WaitAsync(FillTimeout);
            Assert.Equal(OrderSide.Sell, fill.Side);
        }
        catch (HttpRequestException)
        {
            Assert.Skip("Binance testnet rejected STOP_LOSS order — filter restriction.");
        }
    }

    [Fact]
    public async Task StopLimitBuy_ReceivesFill()
    {
        if (!BinanceTestnetCredentials.IsConfigured)
            Assert.Skip(BinanceTestnetCredentials.SkipReason);

        var stopPrice = ReferencePrice - (long)(500m / fixture.Asset!.TickSize);
        var limitPrice = ReferencePrice + (long)(100m / fixture.Asset!.TickSize);

        Strategy.ResetFillTcs();
        try
        {
            Strategy.OnNextBar = orders =>
            {
                orders.Submit(new Order
                {
                    Id = 0,
                    Asset = fixture.Asset!,
                    Side = OrderSide.Buy,
                    Type = OrderType.StopLimit,
                    Quantity = MinQty,
                    StopPrice = stopPrice,
                    LimitPrice = limitPrice,
                });
            };

            var fill = await Strategy.NextFillTcs.Task.WaitAsync(FillTimeout);
            Assert.Equal(OrderSide.Buy, fill.Side);
        }
        catch (HttpRequestException)
        {
            Assert.Skip("Binance testnet rejected STOP_LOSS_LIMIT order — filter restriction.");
        }
    }

    [Fact]
    public async Task StopLimitSell_ReceivesFill()
    {
        if (!BinanceTestnetCredentials.IsConfigured)
            Assert.Skip(BinanceTestnetCredentials.SkipReason);

        // Buy first
        Strategy.ResetFillTcs();
        Strategy.OnNextBar = orders =>
        {
            orders.Submit(new Order
            {
                Id = 0,
                Asset = fixture.Asset!,
                Side = OrderSide.Buy,
                Type = OrderType.Market,
                Quantity = MinQty,
            });
        };
        await Strategy.NextFillTcs.Task.WaitAsync(FillTimeout);

        var stopPrice = ReferencePrice + (long)(500m / fixture.Asset!.TickSize);
        var limitPrice = ReferencePrice - (long)(100m / fixture.Asset!.TickSize);

        Strategy.ResetFillTcs();
        try
        {
            Strategy.OnNextBar = orders =>
            {
                orders.Submit(new Order
                {
                    Id = 0,
                    Asset = fixture.Asset!,
                    Side = OrderSide.Sell,
                    Type = OrderType.StopLimit,
                    Quantity = MinQty,
                    StopPrice = stopPrice,
                    LimitPrice = limitPrice,
                });
            };

            var fill = await Strategy.NextFillTcs.Task.WaitAsync(FillTimeout);
            Assert.Equal(OrderSide.Sell, fill.Side);
        }
        catch (HttpRequestException)
        {
            Assert.Skip("Binance testnet rejected STOP_LOSS_LIMIT order — filter restriction.");
        }
    }
}
