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

    public virtual Task<BacktestResult> RunAsync(
        TimeSeries<IntBar> bars,
        IIntBarStrategy strategy,
        BacktestOptions options,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var asset = options.Asset;

        var portfolio = new Portfolio { InitialCash = options.InitialCash };
        portfolio.Initialize();

        var fills = new List<Fill>();
        var barList = new List<IntBar>();
        var orderIdCounter = 0L;
        StrategyAction? pendingAction = null;

        for (var i = 0; i < bars.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var bar = bars[i];
            barList.Add(bar);

            if (pendingAction is not null)
            {
                var order = CreateOrder(pendingAction, ref orderIdCounter, bars.GetTimestamp(i));
                var fill = _barMatcher.TryFill(order, bar, options);
                if (fill is not null)
                {
                    fills.Add(fill);
                    portfolio.Apply(fill);
                }
                pendingAction = null;
            }

            strategy.OnBarComplete(bars);
        }

        var finalPrice = barList.Count > 0 ? barList[^1].Close : 0L;
        var metrics = _metricsCalculator.Calculate(fills, barList, portfolio, finalPrice, asset);

        stopwatch.Stop();
        return Task.FromResult(new BacktestResult(portfolio, fills, barList, metrics, stopwatch.Elapsed));
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
