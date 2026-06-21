using System.Collections.Concurrent;
using System.Threading.Channels;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.Trading;
using AlgoTradeForge.LiveHost.Application.Live;
using AlgoTradeForge.LiveHost.Infrastructure.Live;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live;

public class BoundedChannelSafetyTests
{
    private static readonly CryptoAsset BtcUsdt = CryptoAsset.Create("BTCUSDT", "Binance",
        decimalDigits: 2,
        minOrderQuantity: 0.00001m, maxOrderQuantity: 9000m, quantityStepSize: 0.00001m);

    private sealed class BlockingOrderClient : IExchangeOrderClient
    {
        private readonly TaskCompletionSource _gate;

        public BlockingOrderClient(TaskCompletionSource gate) => _gate = gate;

        public async Task<ExchangeOrderResult> PlaceOrderAsync(
            string symbol, OrderSide side, OrderType type, decimal quantity,
            decimal? price = null, decimal? stopPrice = null, CancellationToken ct = default)
        {
            await _gate.Task.WaitAsync(ct);
            return new ExchangeOrderResult(1, []);
        }

        public Task CancelOrderAsync(string symbol, long orderId, CancellationToken ct = default)
            => _gate.Task.WaitAsync(ct);
    }

    private static Order NewLimitOrder() => new()
    {
        Id = 0,
        Asset = BtcUsdt,
        Side = OrderSide.Buy,
        Type = OrderType.Limit,
        Quantity = 0.001m,
        LimitPrice = 5000000L,
    };

    private static LiveOrderContext CreateContext(IExchangeOrderClient client, int capacity)
    {
        var portfolio = new Portfolio { InitialCash = 100_000_00L };
        portfolio.Initialize();

        return new LiveOrderContext(
            portfolio, BtcUsdt, new OrderValidator(),
            NullLogger.Instance, client,
            Guid.NewGuid(), new ConcurrentDictionary<long, Guid>(),
            channelCapacity: capacity);
    }

    [Fact]
    public void Submit_RejectsOrder_WhenOrderChannelFull_NotSilentlyDropped()
    {
        var gate = new TaskCompletionSource();
        var client = new BlockingOrderClient(gate);
        const int capacity = 2;
        var ctx = CreateContext(client, capacity);
        ctx.Start(CancellationToken.None);

        // The single reader picks up one item and blocks on PlaceOrderAsync (gate not set).
        // After the channel buffer (capacity) fills, the next Submit must reject, not drop.
        var rejected = new List<Order>();
        Order? overflow = null;
        for (var i = 0; i < capacity + 10; i++)
        {
            var order = NewLimitOrder();
            ctx.Submit(order);
            if (order.Status == OrderStatus.Rejected)
            {
                overflow = order;
                rejected.Add(order);
            }
        }

        Assert.NotNull(overflow);
        Assert.Equal(OrderStatus.Rejected, overflow!.Status);
        // Rejected overflow orders must NOT remain in pending (no silent loss / leak).
        Assert.DoesNotContain(ctx.GetPendingOrders(), o => o.Status == OrderStatus.Rejected);

        gate.SetResult();
    }

    [Fact]
    public async Task WriteAsync_IntoFullBoundedChannel_CompletesRoundTrip_WhenReaderDrains()
    {
        // This proves the deadlock fix mechanism used in BinanceLiveConnector.RunReconciliationLoopAsync:
        // a bounded Channel<Action> that is full does NOT hang a marshal-and-await round trip when the
        // write uses WriteAsync (which awaits a slot) instead of TryWrite (which drops on full).
        const int capacity = 2;
        var queue = Channel.CreateBounded<Action>(
            new BoundedChannelOptions(capacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var ct = cts.Token;

        var readerGate = new TaskCompletionSource();

        // Independent single-reader drain, paused until we say go.
        var processingTask = Task.Run(async () =>
        {
            await readerGate.Task.WaitAsync(ct);
            await foreach (var action in queue.Reader.ReadAllAsync(ct))
                action();
        }, ct);

        // Saturate the bounded buffer while the reader is paused.
        for (var i = 0; i < capacity; i++)
            Assert.True(queue.Writer.TryWrite(() => { }));

        // Channel is now full: TryWrite would fail/drop here.
        Assert.False(queue.Writer.TryWrite(() => { }));

        // The reconciliation pattern: marshal an action that completes a tcs, then await it.
        var tcs = new TaskCompletionSource();
        var writeAndAwait = Task.Run(async () =>
        {
            await queue.Writer.WriteAsync(() => tcs.SetResult(), ct);
            await tcs.Task;
        }, ct);

        // Release the reader: it drains the buffer, eventually runs our marshaled action.
        readerGate.SetResult();

        await writeAndAwait.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.True(tcs.Task.IsCompletedSuccessfully,
            "WriteAsync-into-a-full-bounded-channel round trip must complete once the reader drains");

        queue.Writer.TryComplete();
        await processingTask.WaitAsync(TimeSpan.FromSeconds(5), ct);
    }
}
