using System.Diagnostics;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Reporting;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Engine;

public static class BacktestEngine
{
    public static async Task<BacktestResult> RunAsync(
        IBarSource source,
        IBarStrategy strategy,
        BacktestOptions options,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();

        var portfolio = new Portfolio { InitialCash = options.InitialCash };
        portfolio.Initialize();

        var fills = new List<Fill>();
        var bars = new List<OhlcvBar>();
        var orderIdCounter = 0L;
        StrategyAction? pendingAction = null;

        await foreach (var bar in source.GetBarsAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            bars.Add(bar);

            if (pendingAction is not null)
            {
                var order = CreateOrder(pendingAction, ref orderIdCounter, bar.Timestamp);
                var fill = BarMatcher.TryFill(order, bar, options);

                if (fill is not null)
                {
                    order.Status = OrderStatus.Filled;
                    portfolio.Apply(fill);
                    fills.Add(fill);
                }
                else
                {
                    order.Status = OrderStatus.Rejected;
                }

                pendingAction = null;
            }

            var context = new StrategyContext(bar, bars.Count - 1, portfolio, fills, bars);
            pendingAction = strategy.OnBar(context);
        }

        var finalPrice = bars.Count > 0 ? bars[^1].Close : 0m;
        var metrics = MetricsCalculator.Calculate(fills, bars, portfolio, finalPrice);

        stopwatch.Stop();
        return new BacktestResult(portfolio, fills, bars, metrics, stopwatch.Elapsed);
    }

    private static Order CreateOrder(StrategyAction action, ref long orderIdCounter, DateTimeOffset timestamp)
    {
        return new Order
        {
            Id = ++orderIdCounter,
            Side = action.Side,
            Type = action.Type,
            Quantity = action.Quantity,
            LimitPrice = action.LimitPrice,
            SubmittedAt = timestamp
        };
    }
}
