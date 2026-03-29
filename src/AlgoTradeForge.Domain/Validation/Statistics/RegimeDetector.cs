using AlgoTradeForge.Domain.Validation.Results;

namespace AlgoTradeForge.Domain.Validation.Statistics;

/// <summary>
/// Detects performance regimes using rolling volatility with percentile-based
/// classification (Bull/Bear/Sideways). Uses a simple fixed method to avoid
/// overfitting the detector itself (no HMM).
/// </summary>
public static class RegimeDetector
{
    /// <summary>
    /// Analyzes P&amp;L deltas to detect performance regimes and compute per-regime metrics.
    /// </summary>
    /// <param name="pnlDeltas">Per-bar P&amp;L deltas.</param>
    /// <param name="initialEquity">Starting equity for return computation.</param>
    /// <param name="volWindow">Rolling window size for volatility and mean computation.</param>
    /// <param name="annualizationFactor">Bars per year for Sharpe annualization.</param>
    public static RegimeAnalysisResult Analyze(
        ReadOnlySpan<double> pnlDeltas,
        double initialEquity,
        int volWindow = 60,
        double annualizationFactor = 365)
    {
        if (pnlDeltas.Length <= volWindow)
        {
            return new RegimeAnalysisResult
            {
                Regimes = [],
                ProfitableRegimeCount = 0,
                SharpeRange = (0, 0),
            };
        }

        var n = pnlDeltas.Length;

        // Compute per-bar returns and equity prefix sums for O(1) segment lookups
        var returns = new double[n];
        var equityPrefix = new double[n + 1];
        equityPrefix[0] = initialEquity;
        for (var i = 0; i < n; i++)
        {
            returns[i] = equityPrefix[i] > 0 ? pnlDeltas[i] / equityPrefix[i] : 0.0;
            equityPrefix[i + 1] = equityPrefix[i] + pnlDeltas[i];
        }

        // Compute rolling volatility and rolling mean
        var rollingVol = new double[n - volWindow];
        var rollingMean = new double[n - volWindow];

        for (var i = volWindow; i < n; i++)
        {
            var idx = i - volWindow;
            var sum = 0.0;
            var sumSq = 0.0;
            for (var j = i - volWindow; j < i; j++)
            {
                sum += returns[j];
                sumSq += returns[j] * returns[j];
            }

            var mean = sum / volWindow;
            var variance = sumSq / volWindow - mean * mean;
            rollingVol[idx] = Math.Sqrt(Math.Max(0, variance));
            rollingMean[idx] = mean;
        }

        // Compute percentile thresholds for classification
        var sortedVol = (double[])rollingVol.Clone();
        Array.Sort(sortedVol);
        var p33 = StatisticalUtils.GetPercentile(sortedVol, 33);
        var p67 = StatisticalUtils.GetPercentile(sortedVol, 67);

        // Classify each bar after warmup
        var labels = new string[rollingVol.Length];
        for (var i = 0; i < rollingVol.Length; i++)
        {
            if (rollingVol[i] <= p33 && rollingMean[i] > 0)
                labels[i] = "Bull";
            else if (rollingVol[i] >= p67 && rollingMean[i] < 0)
                labels[i] = "Bear";
            else
                labels[i] = "Sideways";
        }

        // Merge consecutive same-label bars into segments
        var segments = new List<RegimeSegment>();
        var segStart = 0;
        for (var i = 1; i <= labels.Length; i++)
        {
            if (i == labels.Length || labels[i] != labels[segStart])
            {
                var barStart = segStart + volWindow; // Offset by warmup
                var barEnd = i + volWindow;

                // Compute metrics for this segment from pnlDeltas
                var segPnl = pnlDeltas.Slice(barStart, barEnd - barStart);
                var metrics = ComputeSegmentMetrics(segPnl, equityPrefix[barStart], annualizationFactor);

                segments.Add(new RegimeSegment
                {
                    Label = labels[segStart],
                    StartBar = barStart,
                    EndBar = barEnd,
                    Sharpe = metrics.Sharpe,
                    ReturnPct = metrics.ReturnPct,
                    MaxDrawdownPct = metrics.MaxDrawdownPct,
                });

                if (i < labels.Length)
                    segStart = i;
            }
        }

        var profitableCount = segments.Count(s => s.ReturnPct > 0);
        var minSharpe = segments.Count > 0 ? segments.Min(s => s.Sharpe) : 0;
        var maxSharpe = segments.Count > 0 ? segments.Max(s => s.Sharpe) : 0;

        return new RegimeAnalysisResult
        {
            Regimes = segments,
            ProfitableRegimeCount = profitableCount,
            SharpeRange = (minSharpe, maxSharpe),
        };
    }

    private static (double Sharpe, double ReturnPct, double MaxDrawdownPct) ComputeSegmentMetrics(
        ReadOnlySpan<double> segmentPnl,
        double equityAtStart,
        double annualizationFactor)
    {
        if (segmentPnl.IsEmpty)
            return (0, 0, 0);

        var n = segmentPnl.Length;
        var sumReturn = 0.0;
        var sumReturnSq = 0.0;
        var eq = equityAtStart;
        var peak = equityAtStart;
        var maxDdPct = 0.0;
        var totalPnl = 0.0;

        for (var i = 0; i < n; i++)
        {
            var ret = eq > 0 ? segmentPnl[i] / eq : 0.0;
            sumReturn += ret;
            sumReturnSq += ret * ret;

            eq += segmentPnl[i];
            totalPnl += segmentPnl[i];

            if (eq > peak)
                peak = eq;

            if (peak > 0)
            {
                var dd = (peak - eq) / peak * 100.0;
                if (dd > maxDdPct)
                    maxDdPct = dd;
            }
        }

        var meanReturn = sumReturn / n;
        var variance = sumReturnSq / n - meanReturn * meanReturn;
        var stdev = Math.Sqrt(Math.Max(0, variance));
        var sharpe = stdev > 0 ? (meanReturn / stdev) * Math.Sqrt(annualizationFactor) : 0.0;
        var returnPct = equityAtStart > 0 ? totalPnl / equityAtStart * 100.0 : 0.0;

        return (sharpe, returnPct, maxDdPct);
    }

}
