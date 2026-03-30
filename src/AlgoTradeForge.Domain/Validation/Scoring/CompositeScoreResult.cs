namespace AlgoTradeForge.Domain.Validation.Scoring;

/// <summary>
/// Output of the composite validation scorer. Contains the overall score (0–100),
/// traffic-light verdict, human-readable summary, hard rejection codes, and
/// per-category sub-scores for dashboard rendering.
/// </summary>
public sealed record CompositeScoreResult(
    double CompositeScore,
    string Verdict,
    string VerdictSummary,
    IReadOnlyList<string> Rejections,
    IReadOnlyDictionary<string, double> CategoryScores)
{
    /// <summary>Category key: Data sufficiency (Stage 1).</summary>
    public const string CategoryData = "Data";

    /// <summary>Category key: Statistical significance (Stage 2).</summary>
    public const string CategoryStats = "Stats";

    /// <summary>Category key: Parameter landscape (Stage 3).</summary>
    public const string CategoryParams = "Params";

    /// <summary>Category key: Walk-forward optimization (Stage 4).</summary>
    public const string CategoryWfo = "WFO";

    /// <summary>Category key: Walk-forward matrix (Stage 5).</summary>
    public const string CategoryWfm = "WFM";

    /// <summary>Category key: Monte Carlo &amp; permutation (Stage 6).</summary>
    public const string CategoryMc = "MC";

    /// <summary>Category key: Sub-period consistency &amp; decay (Stage 7).</summary>
    public const string CategorySubPeriod = "SubPeriod";

    public const string VerdictGreen = "Green";
    public const string VerdictYellow = "Yellow";
    public const string VerdictRed = "Red";
}
