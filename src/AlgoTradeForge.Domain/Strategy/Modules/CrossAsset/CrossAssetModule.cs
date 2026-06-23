using AlgoTradeForge.Domain.Strategy.Subscriptions;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Optimization.Attributes;

namespace AlgoTradeForge.Domain.Strategy.Modules.CrossAsset;

/// <summary>
/// Cross-asset module for pairs trading. Computes z-score of the log-spread,
/// hedge ratio, and simplified cointegration status.
/// Writes results to the ICrossAssetContext.
/// </summary>
[ModuleKey("cross-asset")]
public sealed class CrossAssetModule(CrossAssetParams parameters)
    : IStrategyModule<CrossAssetParams>
{
    private DataFeedSubscription _sub1 = null!;
    private DataFeedSubscription _sub2 = null!;
    private readonly List<double> _prices1 = [];
    private readonly List<double> _prices2 = [];

    public void Initialize(IIndicatorFactory factory, DataFeedSubscription sub1, DataFeedSubscription sub2)
    {
        _sub1 = sub1;
        _sub2 = sub2;
    }

    public void Update(Int64Bar bar, DataFeedSubscription sub, ICrossAssetContext context)
    {
        if (sub == _sub1)
            _prices1.Add(bar.Close);
        else if (sub == _sub2)
            _prices2.Add(bar.Close);

        // Need matching data from both series
        var count = Math.Min(_prices1.Count, _prices2.Count);
        if (count < parameters.LookbackPeriod)
            return;

        // Compute hedge ratio (simple regression slope over lookback window)
        var startIdx = count - parameters.LookbackPeriod;
        var hedgeRatio = ComputeHedgeRatio(startIdx, count);
        context.HedgeRatio = hedgeRatio;

        // Compute spread: log(A) - hedgeRatio * log(B)
        var spreads = new double[parameters.LookbackPeriod];
        for (var i = 0; i < parameters.LookbackPeriod; i++)
        {
            var idx = startIdx + i;
            spreads[i] = Math.Log(_prices1[idx]) - hedgeRatio * Math.Log(_prices2[idx]);
        }

        // Z-score of spread
        var mean = spreads.Average();
        var variance = spreads.Select(s => (s - mean) * (s - mean)).Average();
        var stddev = Math.Sqrt(variance);

        var currentSpread = spreads[^1];
        var zScore = stddev > 0 ? (currentSpread - mean) / stddev : 0;
        context.ZScore = zScore;

        // Simplified cointegration check: high correlation + bounded spread
        var correlation = ComputeCorrelation(startIdx, count);
        var isCointegrated = Math.Abs(correlation) > 0.7 && stddev < Math.Abs(mean) * 2;
        context.IsCointegrated = isCointegrated;

        // Trim consumed entries to bound memory — keep only the lookback window
        TrimPriceLists();
    }

    private void TrimPriceLists()
    {
        var excess = Math.Min(_prices1.Count, _prices2.Count) - parameters.LookbackPeriod;
        if (excess <= parameters.LookbackPeriod)
            return;

        _prices1.RemoveRange(0, excess);
        _prices2.RemoveRange(0, excess);
    }

    private double ComputeHedgeRatio(int startIdx, int endIdx)
    {
        var n = endIdx - startIdx;
        double sumX = 0, sumY = 0, sumXY = 0, sumXX = 0;

        for (var i = startIdx; i < endIdx; i++)
        {
            var x = Math.Log(_prices2[i]);
            var y = Math.Log(_prices1[i]);
            sumX += x;
            sumY += y;
            sumXY += x * y;
            sumXX += x * x;
        }

        var denom = n * sumXX - sumX * sumX;
        return denom != 0 ? (n * sumXY - sumX * sumY) / denom : 1.0;
    }

    private double ComputeCorrelation(int startIdx, int endIdx)
    {
        var n = endIdx - startIdx;
        double sum1 = 0, sum2 = 0, sum1Sq = 0, sum2Sq = 0, sumProd = 0;

        for (var i = startIdx; i < endIdx; i++)
        {
            var a = _prices1[i];
            var b = _prices2[i];
            sum1 += a;
            sum2 += b;
            sum1Sq += a * a;
            sum2Sq += b * b;
            sumProd += a * b;
        }

        var num = n * sumProd - sum1 * sum2;
        var den = Math.Sqrt((n * sum1Sq - sum1 * sum1) * (n * sum2Sq - sum2 * sum2));
        return den > 0 ? num / den : 0;
    }
}
