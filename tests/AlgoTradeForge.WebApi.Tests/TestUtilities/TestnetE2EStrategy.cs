using System.Collections.Concurrent;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.WebApi.Tests.TestUtilities;

internal sealed class TestnetE2EStrategyParams : StrategyParamsBase;

internal sealed class TestnetE2EStrategy(TestnetE2EStrategyParams p)
    : StrategyBase<TestnetE2EStrategyParams>(p)
{
    public override string Version => "1.0.0";

    private const decimal MinQty = 0.00010m;

    public ConcurrentBag<(string AssetName, Int64Bar Bar)> BarsReceived { get; } = [];
    public ConcurrentBag<Fill> FillsReceived { get; } = [];

    public TaskCompletionSource<bool> BothAssetsReceived { get; } = new();
    public TaskCompletionSource<bool> OrderTestsCompleted { get; } = new();
    public Exception? TestException { get; private set; }

    private readonly ConcurrentDictionary<string, bool> _assetsSeen = new();
    private int _bothConfirmed;
    private int _orderPhase; // 0=waiting, 1=buy submitted, 2=sell submitted

    public override void OnBarComplete(Int64Bar bar, DataSubscription subscription, IOrderContext orders)
    {
        var assetName = subscription.Asset.Name;
        BarsReceived.Add((assetName, bar));
        _assetsSeen.TryAdd(assetName, true);

        if (_assetsSeen.Count >= 2)
            BothAssetsReceived.TrySetResult(true);

        // After both assets confirmed, submit market buy on next bar for primary asset
        if (_assetsSeen.Count >= 2 &&
            Interlocked.CompareExchange(ref _bothConfirmed, 1, 0) == 0)
        {
            return; // Skip this bar, submit on next
        }

        if (_bothConfirmed == 1 &&
            assetName == "BTCUSDT" &&
            Interlocked.CompareExchange(ref _orderPhase, 1, 0) == 0)
        {
            try
            {
                orders.Submit(new Order
                {
                    Id = 0,
                    Asset = subscription.Asset,
                    Side = OrderSide.Buy,
                    Type = OrderType.Market,
                    Quantity = MinQty,
                });
            }
            catch (Exception ex)
            {
                TestException = ex;
                OrderTestsCompleted.TrySetException(ex);
            }
        }
    }

    public override void OnTrade(Fill fill, Order order, IOrderContext orders)
    {
        FillsReceived.Add(fill);

        try
        {
            if (fill.Side == OrderSide.Buy &&
                Interlocked.CompareExchange(ref _orderPhase, 2, 1) == 1)
            {
                // Sell to close
                orders.Submit(new Order
                {
                    Id = 0,
                    Asset = fill.Asset,
                    Side = OrderSide.Sell,
                    Type = OrderType.Market,
                    Quantity = fill.Quantity,
                });
            }
            else if (fill.Side == OrderSide.Sell && _orderPhase == 2)
            {
                OrderTestsCompleted.TrySetResult(true);
            }
        }
        catch (Exception ex)
        {
            TestException = ex;
            OrderTestsCompleted.TrySetException(ex);
        }
    }
}
