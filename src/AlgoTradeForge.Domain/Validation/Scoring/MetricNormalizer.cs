namespace AlgoTradeForge.Domain.Validation.Scoring;

/// <summary>
/// Piecewise-linear normalization of heterogeneous metrics to a 0–100 scale.
/// </summary>
public static class MetricNormalizer
{
    /// <summary>
    /// Normalizes a "higher is better" metric to 0–100.
    /// Returns 0 at <paramref name="floor"/>, 100 at <paramref name="excellent"/>, linear between, clamped.
    /// </summary>
    public static double Normalize(double value, double floor, double excellent)
    {
        var sanitized = Sanitize(value);
        if (sanitized <= floor) return 0.0;
        if (sanitized >= excellent) return 100.0;
        return (sanitized - floor) / (excellent - floor) * 100.0;
    }

    /// <summary>
    /// Normalizes a "lower is better" metric to 0–100.
    /// <paramref name="floor"/> is the worst value (score=0), <paramref name="excellent"/> is the best value (score=100).
    /// Floor &gt; excellent for inverted metrics (e.g., ddMultiplier: floor=3.0, excellent=1.0).
    /// </summary>
    public static double NormalizeInverted(double value, double floor, double excellent)
    {
        if (double.IsNaN(value) || double.IsNegativeInfinity(value)) return 0.0;
        if (double.IsPositiveInfinity(value)) return 0.0;
        if (value >= floor) return 0.0;
        if (value <= excellent) return 100.0;
        return (floor - value) / (floor - excellent) * 100.0;
    }

    private static double Sanitize(double value)
    {
        if (double.IsNaN(value)) return double.NegativeInfinity;
        return value;
    }
}
