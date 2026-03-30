namespace AlgoTradeForge.Domain.Validation.Statistics;

/// <summary>
/// Implements the Probabilistic Sharpe Ratio (PSR) and Deflated Sharpe Ratio (DSR)
/// from Bailey &amp; Lopez de Prado (2014).
/// </summary>
public static class ProbabilisticSharpeRatio
{
    private const double EulerMascheroni = 0.5772156649015329;

    /// <summary>
    /// Computes the Probabilistic Sharpe Ratio — the probability that the observed Sharpe
    /// exceeds a benchmark, accounting for non-normal return distributions.
    /// </summary>
    /// <returns>PSR as a probability in [0, 1].</returns>
    public static double ComputePSR(
        double observedSharpe,
        double benchmarkSharpe,
        int sampleSize,
        double skewness,
        double excessKurtosis)
    {
        if (sampleSize < 2) return 0.0;

        var sr = observedSharpe;
        var denominator = 1.0
            - skewness * sr
            + (excessKurtosis - 1.0) / 4.0 * sr * sr;

        if (denominator <= 0) return sr > benchmarkSharpe ? 1.0 : 0.0;

        var z = (sr - benchmarkSharpe) * Math.Sqrt(sampleSize - 1) / Math.Sqrt(denominator);
        return StandardNormalCdf(z);
    }

    /// <summary>
    /// Computes the Deflated Sharpe Ratio — adjusts the benchmark for the number of trials
    /// to correct for multiple-testing bias. Uses E[max(SR)] as the benchmark.
    /// </summary>
    /// <returns>DSR as a probability in [0, 1]. Values ≥ 0.95 suggest significance.</returns>
    public static double ComputeDSR(
        double observedSharpe,
        int trialCount,
        int sampleSize,
        double skewness,
        double excessKurtosis)
    {
        if (trialCount < 1 || sampleSize < 2) return 0.0;

        // Variance of Sharpe ratio estimator
        var srVariance = (1.0
            - skewness * observedSharpe
            + (excessKurtosis - 1.0) / 4.0 * observedSharpe * observedSharpe)
            / (sampleSize - 1);

        if (srVariance <= 0) srVariance = 1e-10;

        var srStd = Math.Sqrt(srVariance);

        // E[max(SR)] ≈ sqrt(V(SR)) * ((1-γ)*Φ⁻¹(1-1/N) + γ*Φ⁻¹(1-1/(N*e)))
        double expectedMaxSr;
        if (trialCount == 1)
        {
            expectedMaxSr = 0.0;
        }
        else
        {
            var n = (double)trialCount;
            var p1 = 1.0 - 1.0 / n;
            var p2 = 1.0 - 1.0 / (n * Math.E);

            // Clamp probabilities to avoid infinite quantiles
            p1 = Math.Clamp(p1, 1e-10, 1.0 - 1e-10);
            p2 = Math.Clamp(p2, 1e-10, 1.0 - 1e-10);

            expectedMaxSr = srStd * (
                (1.0 - EulerMascheroni) * StandardNormalQuantile(p1) +
                EulerMascheroni * StandardNormalQuantile(p2));
        }

        return ComputePSR(observedSharpe, expectedMaxSr, sampleSize, skewness, excessKurtosis);
    }

    /// <summary>
    /// Standard normal CDF. Uses the Abramowitz &amp; Stegun approximation (7.1.26).
    /// </summary>
    public static double StandardNormalCdf(double x)
    {
        if (x < -8.0) return 0.0;
        if (x > 8.0) return 1.0;

        // Horner form of the rational approximation
        const double a1 = 0.254829592;
        const double a2 = -0.284496736;
        const double a3 = 1.421413741;
        const double a4 = -1.453152027;
        const double a5 = 1.061405429;
        const double p = 0.3275911;

        var sign = x < 0 ? -1 : 1;
        var absX = Math.Abs(x) / Math.Sqrt(2.0);

        var t = 1.0 / (1.0 + p * absX);
        var erf = 1.0 - (((((a5 * t + a4) * t) + a3) * t + a2) * t + a1) * t * Math.Exp(-absX * absX);

        return 0.5 * (1.0 + sign * erf);
    }

    /// <summary>
    /// Inverse standard normal CDF using Acklam's rational approximation.
    /// Accurate to ~1.15e-9 across the full range.
    /// </summary>
    public static double StandardNormalQuantile(double p)
    {
        if (p <= 0) return double.NegativeInfinity;
        if (p >= 1) return double.PositiveInfinity;
        if (Math.Abs(p - 0.5) < 1e-15) return 0.0;

        // Coefficients for rational approximation
        const double a1 = -3.969683028665376e+01;
        const double a2 = 2.209460984245205e+02;
        const double a3 = -2.759285104469687e+02;
        const double a4 = 1.383577518672690e+02;
        const double a5 = -3.066479806614716e+01;
        const double a6 = 2.506628277459239e+00;

        const double b1 = -5.447609879822406e+01;
        const double b2 = 1.615858368580409e+02;
        const double b3 = -1.556989798598866e+02;
        const double b4 = 6.680131188771972e+01;
        const double b5 = -1.328068155288572e+01;

        const double c1 = -7.784894002430293e-03;
        const double c2 = -3.223964580411365e-01;
        const double c3 = -2.400758277161838e+00;
        const double c4 = -2.549732539343734e+00;
        const double c5 = 4.374664141464968e+00;
        const double c6 = 2.938163982698783e+00;

        const double d1 = 7.784695709041462e-03;
        const double d2 = 3.224671290700398e-01;
        const double d3 = 2.445134137142996e+00;
        const double d4 = 3.754408661907416e+00;

        const double pLow = 0.02425;
        const double pHigh = 1.0 - pLow;

        double q, r;

        if (p < pLow)
        {
            // Rational approximation for lower region
            q = Math.Sqrt(-2.0 * Math.Log(p));
            return (((((c1 * q + c2) * q + c3) * q + c4) * q + c5) * q + c6) /
                   ((((d1 * q + d2) * q + d3) * q + d4) * q + 1.0);
        }

        if (p <= pHigh)
        {
            // Rational approximation for central region
            q = p - 0.5;
            r = q * q;
            return (((((a1 * r + a2) * r + a3) * r + a4) * r + a5) * r + a6) * q /
                   (((((b1 * r + b2) * r + b3) * r + b4) * r + b5) * r + 1.0);
        }

        // Rational approximation for upper region
        q = Math.Sqrt(-2.0 * Math.Log(1.0 - p));
        return -(((((c1 * q + c2) * q + c3) * q + c4) * q + c5) * q + c6) /
               ((((d1 * q + d2) * q + d3) * q + d4) * q + 1.0);
    }
}
