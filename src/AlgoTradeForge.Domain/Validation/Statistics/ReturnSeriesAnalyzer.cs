namespace AlgoTradeForge.Domain.Validation.Statistics;

/// <summary>
/// Computes log returns and distributional moments (skewness, excess kurtosis)
/// from equity curve data.
/// </summary>
public static class ReturnSeriesAnalyzer
{
    /// <summary>
    /// Computes log returns: ln(equity[i] / equity[i-1]).
    /// Returns array of length (equityCurve.Length - 1).
    /// Skips any pair where equity[i-1] ≤ 0 (sets return to 0).
    /// </summary>
    public static double[] ComputeLogReturns(ReadOnlySpan<double> equityCurve)
    {
        if (equityCurve.Length < 2) return [];

        var returns = new double[equityCurve.Length - 1];
        for (var i = 1; i < equityCurve.Length; i++)
        {
            if (equityCurve[i - 1] > 0 && equityCurve[i] > 0)
                returns[i - 1] = Math.Log(equityCurve[i] / equityCurve[i - 1]);
            else
                returns[i - 1] = 0.0;
        }

        return returns;
    }

    /// <summary>
    /// Computes sample skewness and excess kurtosis from a return series.
    /// Uses the standard unbiased estimators.
    /// </summary>
    public static (double Skewness, double ExcessKurtosis) ComputeMoments(ReadOnlySpan<double> returns)
    {
        if (returns.Length < 4) return (0.0, 0.0);

        var n = returns.Length;

        // Mean
        var sum = 0.0;
        for (var i = 0; i < n; i++) sum += returns[i];
        var mean = sum / n;

        // Central moments
        var m2 = 0.0;
        var m3 = 0.0;
        var m4 = 0.0;
        for (var i = 0; i < n; i++)
        {
            var d = returns[i] - mean;
            var d2 = d * d;
            m2 += d2;
            m3 += d2 * d;
            m4 += d2 * d2;
        }

        m2 /= n;
        m3 /= n;
        m4 /= n;

        if (m2 < 1e-20) return (0.0, 0.0);

        var stdDev = Math.Sqrt(m2);
        var skewness = m3 / (stdDev * stdDev * stdDev);
        var kurtosis = m4 / (m2 * m2);
        var excessKurtosis = kurtosis - 3.0;

        return (skewness, excessKurtosis);
    }
}
