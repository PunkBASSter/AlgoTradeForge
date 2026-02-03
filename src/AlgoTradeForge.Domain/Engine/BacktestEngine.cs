using System.Diagnostics;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Reporting;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Engine;

public class BacktestEngine
{
    private readonly IBarMatcher _barMatcher;
    private readonly IMetricsCalculator _metricsCalculator;

    public BacktestEngine(IBarMatcher barMatcher, IMetricsCalculator metricsCalculator)
    {
        _barMatcher = barMatcher;
        _metricsCalculator = metricsCalculator;
    }

    public virtual async Task<BacktestResult> RunAsync(
        IBarSource source,
        IBarStrategy strategy,
        BacktestOptions options,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var asset = options.Asset;

        var portfolio = new Portfolio { InitialCash = options.InitialCash };
        portfolio.Initialize();

        var fills = new List<Fill>();
        var bars = new List<OhlcvBar>();
        var orderIdCounter = 0L;
        StrategyAction? pendingAction = null;

        await foreach (var bar in source.GetBarsAsync(asset.Name, options.StartTime, options.EndTime, ct))
        {
            ct.ThrowIfCancellationRequested();
            bars.Add(bar);

            if (pendingAction is not null)
            {
                var order = CreateOrder(pendingAction, ref orderIdCounter, bar.Timestamp);
                var fill = _barMatcher.TryFill(order, bar, options);

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

            var context = new StrategyContext(asset, bar, bars.Count - 1, portfolio, fills, bars);
            pendingAction = strategy.OnBar(context);
        }

        var finalPrice = bars.Count > 0 ? bars[^1].Close : 0m;
        var metrics = _metricsCalculator.Calculate(fills, bars, portfolio, finalPrice, asset);

        stopwatch.Stop();
        return new BacktestResult(portfolio, fills, bars, metrics, stopwatch.Elapsed);
    }

    protected virtual Order CreateOrder(StrategyAction action, ref long orderIdCounter, DateTimeOffset timestamp)
    {
        return new Order
        {
            Id = ++orderIdCounter,
            Asset = action.Asset,
            Side = action.Side,
            Type = action.Type,
            Quantity = action.Quantity,
            LimitPrice = action.LimitPrice,
            SubmittedAt = timestamp
        };
    }
}
